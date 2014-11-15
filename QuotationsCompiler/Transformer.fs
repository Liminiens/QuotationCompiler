﻿module QuotationsCompiler.Transformer

open System
open System.Reflection

open Microsoft.FSharp.Reflection

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Quotations.ExprShape

open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Range

let inline notImpl<'T> msg : 'T = raise <| new NotImplementedException(msg)

type TransformerState =
    {
        Dependencies : Map<string, Assembly>
    }
with
    static member Empty = { Dependencies = Map.empty }

let updateDependencies (state : TransformerState) (t : Type) =
    let key = t.Assembly.FullName
    match state.Dependencies.TryFind key with
    | Some _ -> state
    | None -> { state with Dependencies = Map.add key t.Assembly state.Dependencies }

let rec sysTypeToSynType (t : System.Type) : SynType =
    if FSharpType.IsTuple t then
        let telems = 
            FSharpType.GetTupleElements t 
            |> Array.toList
            |> List.map(fun et -> false, sysTypeToSynType t)

        SynType.Tuple(telems, range0)
    elif FSharpType.IsFunction t then
        let dom, cod = FSharpType.GetFunctionElements t
        let synDom, synCod = sysTypeToSynType dom, sysTypeToSynType cod
        SynType.Fun(synDom, synCod, range0)
    elif t.IsGenericType then
        let synDef = t.GetGenericTypeDefinition() |> sysTypeToSynType
        let synParams = t.GetGenericArguments() |> Seq.map sysTypeToSynType |> Seq.toList
        SynType.App(synDef, None, synParams, [], None, (* isPostFix *) false, range0)
    elif t.IsArray then
        let synElem = t.GetElementType() |> sysTypeToSynType
        let rk = t.GetArrayRank()
        SynType.Array(rk, synElem, range0)
    else
        let longIdent = 
            t.FullName.Split('`').[0].Split([|'.';'+'|]) 
            |> Seq.map(fun n -> new Ident(n, range0))
            |> Seq.toList

        let liwd = LongIdentWithDots(longIdent, [range0])
        SynType.LongIdent liwd

type MemberInfo with
    member m.TryGetCustomAttribute<'Attr when 'Attr :> System.Attribute> () =
        let attrs = m.GetCustomAttributes<'Attr> ()
        if Seq.isEmpty attrs then None
        else
            Some(Seq.head attrs)

let tryGetCurriedFunctionGroupings (m : MethodInfo) =
    match m.TryGetCustomAttribute<CompilationArgumentCountsAttribute> () with
    | None -> None
    | Some a -> Some(a.Counts |> Seq.toList)

let sysMethodToSynMethod (m : MethodInfo) =
    let fsharpFunctionName =
        match m.TryGetCustomAttribute<CompilationSourceNameAttribute> () with
        | None -> m.Name
        | Some a -> a.SourceName

    let longIdent =
        [
            for id in m.DeclaringType.FullName.Split('`').[0].Split([|'.';'+'|]) do
                yield new Ident(id, range0)

            yield new Ident(fsharpFunctionName, range0)
        ]

    let liwd = LongIdentWithDots(longIdent, [range0])
    SynExpr.LongIdent(false, liwd, None, range0)
    
        

let rec exprToAst (expr : Expr) : SynExpr =
    match expr with
    | Value(:? int as i, t) when t = typeof<int> -> SynExpr.Const(SynConst.Int32 i, range0)
    | Value(:? float as i, t) when t = typeof<float> -> SynExpr.Const(SynConst.Double i, range0)
    | Value(:? string as s, t) when t = typeof<string> -> SynExpr.Const(SynConst.String(s, range0), range0)
    | Value (_,t) -> notImpl (sprintf "Value %O" t)
    | Var v ->
        let ident = new Ident(v.Name, range0)
        SynExpr.Ident ident
        
    | Lambda(v, body) ->
        let vType = sysTypeToSynType v.Type
        let spat = SynSimplePat.Id(new Ident(v.Name, range0), None, false ,false ,false, range0)
        let untypedPat = SynSimplePats.SimplePats([spat], range0)
        let typedPat = SynSimplePats.Typed(untypedPat, vType, range0)
        let bodyAst = exprToAst body
        SynExpr.Lambda(false, false, typedPat, bodyAst, range0)

    | LetRecursive(bindings, body) ->
        let mkBinding (v : Var, bind : Expr) =
            let vType = sysTypeToSynType v.Type
            let untypedPat = SynPat.LongIdent(LongIdentWithDots([new Ident(v.Name, range0)], []), None, None, SynConstructorArgs.Pats [], None, range0)
            let typedPat = SynPat.Typed(untypedPat, vType, range0)
            let synBind = exprToAst bind
            let synValData = SynValData.SynValData(None, SynValInfo([[]], SynArgInfo([], false, None)), None)
            SynBinding.Binding(None, SynBindingKind.NormalBinding, false, false, [], PreXmlDoc.Empty, synValData, typedPat, None, synBind, range0, SequencePointInfoForBinding.SequencePointAtBinding range0)

        let bindings = List.map mkBinding bindings
        let synBody = exprToAst body
        SynExpr.LetOrUse(true, false, bindings, synBody, range0)

    | Let(v, bind, body) ->
        let vType = sysTypeToSynType v.Type
        let untypedPat = SynPat.LongIdent(LongIdentWithDots([new Ident(v.Name, range0)], []), None, None, SynConstructorArgs.Pats [], None, range0)
        let typedPat = SynPat.Typed(untypedPat, vType, range0)
        let synBind = exprToAst bind
        let synBody = exprToAst body
        let synValData = SynValData.SynValData(None, SynValInfo([[]], SynArgInfo([], false, None)), None)
        let synBinding = SynBinding.Binding(None, SynBindingKind.NormalBinding, false, false, [], PreXmlDoc.Empty, synValData, typedPat, None, synBind, range0, SequencePointInfoForBinding.SequencePointAtBinding range0)
        SynExpr.LetOrUse(false, false, [synBinding], synBody, range0)

    | Application(left, right) ->
        let synLeft = exprToAst left
        let synRight = exprToAst right
        SynExpr.App(ExprAtomicFlag.NonAtomic, false, synLeft, synRight, range0)

    | IfThenElse(cond, a, b) ->
        let synCond = exprToAst cond
        let synA = exprToAst a
        let synB = exprToAst b
        SynExpr.IfThenElse(synCond, synA, Some synB, SequencePointInfoForBinding.SequencePointAtBinding range0, false, range0, range0) 

    | Call(None, methodInfo, args) ->
        let synArgs = List.map exprToAst args |> List.toArray
        let groupings = defaultArg (tryGetCurriedFunctionGroupings methodInfo) [synArgs.Length]
        // TODO : type app for generic methods
        let rec foldApp (funcExpr : SynExpr, i : int) (grouping : int) =
            let args =
                match grouping with
                | 1 -> synArgs.[i]
                | _ -> SynExpr.Paren(SynExpr.Tuple(Array.toList <| synArgs.[i .. i + grouping], [], range0), range0, None, range0)

            let funcExpr2 = SynExpr.App(ExprAtomicFlag.NonAtomic, false, funcExpr, args, range0)
            funcExpr2, i + grouping

        let synMethod = sysMethodToSynMethod methodInfo
        let callExpr,_ = List.fold foldApp (synMethod, 0) groupings
        callExpr

    | e -> notImpl (sprintf "%O" e)


let synExprToLetBinding (expr : SynExpr) =
    let synValData = SynValData.SynValData(None, SynValInfo([[]], SynArgInfo([], false, None)), None)
    let synPat = SynPat.LongIdent(LongIdentWithDots([new Ident("compiledQuotation", range0)], []), None, None, SynConstructorArgs.Pats [ SynPat.Paren(SynPat.Const(SynConst.Unit, range0), range0)], None, range0)
    let binding = SynBinding.Binding(None, SynBindingKind.NormalBinding, false, false, [], PreXmlDoc.Empty, synValData, synPat, None, expr, range0, SequencePointInfoForBinding.SequencePointAtBinding range0)
    SynModuleDecl.Let(false, [binding], range0)


let letBindingToParsedInput (decl : SynModuleDecl) =
    let modl = SynModuleOrNamespace([new Ident("Test", range0)], true, [decl], PreXmlDoc.Empty,[], None, range0)
    let file = ParsedImplFileInput("/test.fs", false, QualifiedNameOfFile(new Ident("Test", range0)), [],[], [modl],false)
    ParsedInput.ImplFile file


let quotationToParsedInput : Expr -> ParsedInput = exprToAst >> synExprToLetBinding >> letBindingToParsedInput