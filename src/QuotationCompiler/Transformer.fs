﻿module internal QuotationCompiler.Transformer

open System
open System.Reflection

open Microsoft.FSharp.Reflection

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Quotations.ExprShape

open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.Range

open QuotationCompiler.Dependencies

[<Literal>]
let moduleName = "QuotationCompiler"

[<Literal>]
let compiledFunctionName = "compiledQuotation"

let convertExprToAst (expr : Expr) =

    let dependencies = ref Dependencies.Empty
    let append (t : Type) = dependencies := dependencies.Value.Append t

    let rec exprToAst (expr : Expr) : SynExpr =
        let range = defaultArg (tryParseRange expr) range0
        match expr with
        // parse for constants
        | Value(:? bool as b, t) when t = typeof<bool> -> SynExpr.Const(SynConst.Bool b, range)
        | Value(:? byte as b, t) when t = typeof<byte> -> SynExpr.Const(SynConst.Byte b, range)
        | Value(:? sbyte as b, t) when t = typeof<sbyte> -> SynExpr.Const(SynConst.SByte b, range)
        | Value(:? char as c, t) when t = typeof<char> -> SynExpr.Const(SynConst.Char c, range)
        | Value(:? decimal as d, t) when t = typeof<decimal> -> SynExpr.Const(SynConst.Decimal d, range)
        | Value(:? int16 as i, t) when t = typeof<int16> -> SynExpr.Const(SynConst.Int16 i, range)
        | Value(:? int32 as i, t) when t = typeof<int32> -> SynExpr.Const(SynConst.Int32 i, range)
        | Value(:? int64 as i, t) when t = typeof<int64> -> SynExpr.Const(SynConst.Int64 i, range)
        | Value(:? uint16 as i, t) when t = typeof<uint16> -> SynExpr.Const(SynConst.UInt16 i, range)
        | Value(:? uint32 as i, t) when t = typeof<uint32> -> SynExpr.Const(SynConst.UInt32 i, range)
        | Value(:? uint64 as i, t) when t = typeof<uint64> -> SynExpr.Const(SynConst.UInt64 i, range)
        | Value(:? IntPtr as i, t) when t = typeof<IntPtr> -> SynExpr.Const(SynConst.IntPtr(int64 i), range)
        | Value(:? UIntPtr as i, t) when t = typeof<UIntPtr> -> SynExpr.Const(SynConst.UIntPtr(uint64 i), range)
        | Value(:? single as f, t) when t = typeof<single> -> SynExpr.Const(SynConst.Single f, range)
        | Value(:? double as f, t) when t = typeof<double> -> SynExpr.Const(SynConst.Double f, range)
        | Value(:? string as s, t) when t = typeof<string> -> SynExpr.Const(SynConst.String(s, range), range)
        | Value(:? unit, t) when t = typeof<unit> -> SynExpr.Const(SynConst.Unit, range)
        | Value(:? (byte[]) as bs, t) when t = typeof<byte[]> -> SynExpr.Const(SynConst.Bytes(bs, range), range)
        | Value(:? (uint16[]) as is, t) when t = typeof<uint16[]> -> SynExpr.Const(SynConst.UInt16s is, range)
        | Value (_,t) -> raise <| new NotSupportedException(sprintf "Quotation captures closure of type %O." t)
        // Lambda
        | Var v ->
            append v.Type
            let ident = mkIdent range v.Name
            SynExpr.Ident ident
        
        | Lambda(v, body) ->
            append v.Type
            let vType = sysTypeToSynType range v.Type
            let spat = SynSimplePat.Id(mkIdent range v.Name, None, false ,false ,false, range)
            let untypedPat = SynSimplePats.SimplePats([spat], range)
            let typedPat = SynSimplePats.Typed(untypedPat, vType, range)
            let bodyAst = exprToAst body
            SynExpr.Lambda(false, false, typedPat, bodyAst, range)

        | LetRecursive(bindings, body) ->
            let mkBinding (v : Var, bind : Expr) =
                append v.Type
                let vType = sysTypeToSynType range v.Type
                let untypedPat = mkVarPat range v
                let typedPat = SynPat.Typed(untypedPat, vType, range)
                let synBind = exprToAst bind
                mkBinding range typedPat synBind

            let bindings = List.map mkBinding bindings
            let synBody = exprToAst body
            SynExpr.LetOrUse(true, false, bindings, synBody, range)

        | Let(v, bind, body) ->
            append v.Type
            let vType = sysTypeToSynType range v.Type
            let untypedPat = mkVarPat range v
            let typedPat = SynPat.Typed(untypedPat, vType, range)
            let synBind = exprToAst bind
            let synBody = exprToAst body
            let synValData = SynValData.SynValData(None, SynValInfo([[]], SynArgInfo([], false, None)), None)
            let synBinding = SynBinding.Binding(None, SynBindingKind.NormalBinding, false, v.IsMutable, [], PreXmlDoc.Empty, synValData, typedPat, None, synBind, range, SequencePointInfoForBinding.SequencePointAtBinding range)
            SynExpr.LetOrUse(false, false, [synBinding], synBody, range)

        | Application(left, right) ->
            let synLeft = exprToAst left
            let synRight = exprToAst right
            SynExpr.App(ExprAtomicFlag.NonAtomic, false, synLeft, synRight, range)

        | Sequential(left, right) ->
            let synLeft = exprToAst left
            let synRight = exprToAst right
            SynExpr.Sequential(SequencePointInfoForSeq.SequencePointsAtSeq, true, synLeft, synRight, range)

        | TryWith(body, _, _, cv, cb) ->
            let synBody = exprToAst body
            let synPat = mkVarPat range cv
            let synCatch = exprToAst cb
            let synClause = SynMatchClause.Clause(synPat, None, synCatch, range, SequencePointInfoForTarget.SequencePointAtTarget)
            SynExpr.TryWith(synBody, range, [synClause], range, range, SequencePointInfoForTry.SequencePointAtTry range, SequencePointInfoForWith.SequencePointAtWith range)

        | TryFinally(body, finalizer) ->
            let synBody = exprToAst body
            let synFinalizer = exprToAst finalizer
            SynExpr.TryFinally(synBody, synFinalizer, range, SequencePointInfoForTry.SequencePointAtTry range, SequencePointInfoForFinally.SequencePointAtFinally range)

        | IfThenElse(cond, a, b) ->
            let synCond = exprToAst cond
            let synA = exprToAst a
            let synB = exprToAst b
            SynExpr.IfThenElse(synCond, synA, Some synB, SequencePointInfoForBinding.SequencePointAtBinding range, false, range, range)

        | WhileLoop(cond, body) ->
            let synCond = exprToAst cond
            let synBody = exprToAst body
            SynExpr.While(SequencePointAtWhileLoop range, synCond, synBody, range)

        | Coerce(e, t) ->
            append t
            let synExpr = exprToAst e
            let synType = sysTypeToSynType range t
            let uc = SynExpr.Upcast(synExpr, synType, range)
            SynExpr.Paren(uc, range, None, range)

        | TypeTest(expr, t) ->
            append t
            let synExpr = exprToAst expr
            let synTy = sysTypeToSynType range t
            SynExpr.TypeTest(synExpr, synTy, range)

        | NewObject(ctorInfo, args) ->
            append ctorInfo.DeclaringType
            let synType = sysTypeToSynType range ctorInfo.DeclaringType
            let synParam =
                match List.map exprToAst args with
                | [] -> SynExpr.Const(SynConst.Unit, range)
                | [a] -> SynExpr.Paren(a, range, None, range)
                | synParams -> SynExpr.Tuple(synParams, [], range)

            SynExpr.New(false, synType, synParam, range)

        | NewTuple(args) ->
            let synArgs = List.map exprToAst args
            SynExpr.Tuple(synArgs, [], range)

        | NewArray(t, elems) ->
            append t
            let synTy = sysTypeToSynType range t
            let synArrayTy = SynType.Array(1, synTy, range)
            let synElems = List.map exprToAst elems
            let synArray = SynExpr.ArrayOrList(true, synElems, range)
            SynExpr.Typed(synArray, synArrayTy, range)

        | NewRecord(ty, entries) ->
            append ty
            let synTy = sysTypeToSynType range ty
            let fields = FSharpType.GetRecordFields(ty, BindingFlags.NonPublic ||| BindingFlags.Public) |> Array.toList
            let synEntries = List.map exprToAst entries
            let entries = (fields, synEntries) ||> List.map2 (fun f e -> (mkLongIdent range [mkIdent range f.Name], true), Some e, None)
            SynExpr.Record(None, None, entries, range)

        | NewUnionCase(uci, args) ->
            append uci.DeclaringType
            let uciCtor = mkUciCtor range uci
            let synArgs = List.map exprToAst args
            match synArgs with
            | [] -> uciCtor
            | [a] -> SynExpr.App(ExprAtomicFlag.Atomic, false, uciCtor, a, range)
            | _ ->
                let synParam = SynExpr.Tuple(synArgs, [], range)
                SynExpr.App(ExprAtomicFlag.Atomic, false, uciCtor, synParam, range)

        | NewDelegate(t, vars, body) ->
            append t
            let synType = sysTypeToSynType range t
            let synBody = exprToAst body
            let rec mkLambda acc (rest : Var list) =
                match rest with
                | [] -> acc
                | v :: tail ->
                    let vType = sysTypeToSynType range v.Type
                    let spat = SynSimplePat.Id(mkIdent range v.Name, None, false ,false ,false, range)
                    let untypedPat = SynSimplePats.SimplePats([spat], range)
                    let typedPat = SynSimplePats.Typed(untypedPat, vType, range)
                    let synLambda = SynExpr.Lambda(false, false, typedPat, acc, range)
                    mkLambda synLambda tail

            let synAbs = mkLambda synBody (List.rev vars)
            SynExpr.New(false, synType, SynExpr.Paren(synAbs, range, None, range), range)

        | UnionCaseTest(expr, uci) ->
            append uci.DeclaringType
            let synExpr = exprToAst expr
            let uciIdent = SynPat.LongIdent(mkUciIdent range uci, None, None, SynConstructorArgs.Pats [], None, range)
            let matchClause = SynMatchClause.Clause(uciIdent, None, SynExpr.Const(SynConst.Bool true, range0), range, SequencePointInfoForTarget.SuppressSequencePointAtTarget)
            let notMatchClause = SynMatchClause.Clause(SynPat.Wild range0, None, SynExpr.Const(SynConst.Bool false, range0), range, SequencePointInfoForTarget.SuppressSequencePointAtTarget)
            SynExpr.Match(SequencePointInfoForBinding.SequencePointAtBinding range, synExpr, [matchClause ; notMatchClause], false, range)

        | Call(instance, methodInfo, args) ->
            append methodInfo.DeclaringType
            let synArgs = List.map exprToAst args |> List.toArray
            // TODO : need a way to identify F# 'generic values', i.e. typeof<'T>
            // it seems that the only way to do this is by parsing F# assembly signature metadata
            // for now, use a heuristic that happens to hold for FSharp.Core operators
            // but not user-defined values. These are not supported for now.
            let defaultGrouping =
                if Array.isEmpty synArgs && methodInfo.ContainsAttribute<RequiresExplicitTypeArgumentsAttribute> () then []
                else [synArgs.Length]

            let groupings = defaultArg (tryGetCurriedFunctionGroupings methodInfo) defaultGrouping
            let rec foldApp (funcExpr : SynExpr, i : int) (grouping : int) =
                let args =
                    match grouping with
                    | 0 -> SynExpr.Const(SynConst.Unit, range)
                    | 1 -> SynExpr.Paren(synArgs.[i], range, None, range)
                    | _ -> SynExpr.Paren(SynExpr.Tuple(Array.toList <| synArgs.[i .. i + grouping], [], range), range, None, range)

                let funcExpr2 = SynExpr.App(ExprAtomicFlag.NonAtomic, false, funcExpr, args, range)
                funcExpr2, i + grouping

            let synMethod = 
                match instance with
                | None -> sysMemberToSynMember range methodInfo
                | Some inst ->
                    let synInst = exprToAst inst
                    let liwd = mkLongIdent range [mkIdent range methodInfo.Name]
                    SynExpr.DotGet(synInst, range, liwd, range)

            let synMethod =
                if methodInfo.IsGenericMethod then
                    let margs = methodInfo.GetGenericArguments() |> Seq.map (sysTypeToSynType range) |> Seq.toList
                    SynExpr.TypeApp(synMethod, range, margs, [], None, range, range)
                else
                    synMethod

            let callExpr,_ = List.fold foldApp (synMethod, 0) groupings
            callExpr

        | TupleGet(tuple, idx) ->
            let synTuple = exprToAst tuple
            let arity = FSharpType.GetTupleElements(tuple.Type).Length
            let ident = mkIdent range "_item"
            let synIdent = SynExpr.Ident(ident)
            let patterns = 
                [ 
                    for i in 0 .. idx - 1 -> SynPat.Wild range
                    yield SynPat.Named(SynPat.Wild range, ident, false, None, range)
                    for i in idx + 1 .. arity - 1 -> SynPat.Wild range
                ]

            let synPat = SynPat.Tuple(patterns, range)
            let binding = mkBinding range synPat synTuple
            SynExpr.LetOrUse(false, false, [binding], synIdent, range)

        | PropertyGet(instance, propertyInfo, []) ->
            append propertyInfo.DeclaringType
            match instance with
            | None -> sysMemberToSynMember range propertyInfo
            | Some inst ->
                let sysInst = exprToAst inst
                let liwd = mkLongIdent range [mkIdent range propertyInfo.Name]
                SynExpr.DotGet(sysInst, range, liwd, range)

        | PropertyGet(instance, propertyInfo, indexers) ->
            append propertyInfo.DeclaringType
            let synIndexer = 
                match List.map exprToAst indexers with
                | [one] -> SynIndexerArg.One(one)
                | synIdx -> SynIndexerArg.One(SynExpr.Tuple(synIdx, [range], range))

            match instance with
            | None -> 
                let ident = sysMemberToSynMember range propertyInfo.DeclaringType
                SynExpr.DotIndexedGet(ident, [synIndexer], range0, range0)
            | Some inst ->
                let synInst = exprToAst inst
                SynExpr.DotIndexedGet(synInst, [synIndexer], range, range)

        | PropertySet(instance, propertyInfo, [], value) ->
            append propertyInfo.DeclaringType
            let synValue = exprToAst value
            match instance with
            | None ->
                let ident = LongIdentWithDots(getMemberPath range propertyInfo, [])
                SynExpr.LongIdentSet(ident, synValue, range)
            | Some inst ->
                let synInst = exprToAst inst
                SynExpr.DotSet(synInst, LongIdentWithDots([mkIdent range propertyInfo.Name], []), synValue, range)

        | PropertySet(instance, propertyInfo, indexers, value) ->
            append propertyInfo.DeclaringType
            let synValue = exprToAst value
            let synIndexer = 
                match List.map exprToAst indexers with
                | [one] -> SynIndexerArg.One(one)
                | synIdx -> SynIndexerArg.One(SynExpr.Tuple(synIdx, [range], range))

            match instance with
            | None ->
                let ident = sysMemberToSynMember range propertyInfo.DeclaringType
                SynExpr.DotIndexedSet(ident, [synIndexer], synValue, range, range, range)

            | Some inst ->
                let synInst = exprToAst inst
                SynExpr.DotIndexedSet(synInst, [synIndexer], synValue, range, range, range)

        | FieldGet(instance, fieldInfo) ->
            append fieldInfo.DeclaringType
            match instance with
            | None -> sysMemberToSynMember range fieldInfo
            | Some inst ->
                let synInst = exprToAst inst
                SynExpr.DotGet(synInst, range, mkLongIdent range [mkIdent range (getFSharpName fieldInfo)], range)

        | FieldSet(instance, fieldInfo, value) ->
            append fieldInfo.DeclaringType
            let synValue = exprToAst value
            match instance with
            | None ->
                let ident = LongIdentWithDots(getMemberPath range fieldInfo, [])
                SynExpr.LongIdentSet(ident, synValue, range)
            | Some inst ->
                let synInst = exprToAst inst
                SynExpr.DotSet(synInst, LongIdentWithDots([mkIdent range fieldInfo.Name], []), synValue, range)
        
        | VarSet(v, value) ->
            append v.Type
            let synValue = exprToAst value
            let synVar = LongIdentWithDots([mkIdent range v.Name], [])
            SynExpr.LongIdentSet(synVar, synValue, range)

        | ForIntegerRangeLoop(var, startExpr, endExpr, body) ->
            let varIdent = mkIdent range var.Name
            let synStartExpr = exprToAst startExpr
            let synEndExpr = exprToAst endExpr
            let synBody = exprToAst body
            SynExpr.For(SequencePointAtForLoop range, varIdent, synStartExpr, true, synEndExpr, synBody, range)

        | AddressOf e -> notImpl expr
        | AddressSet(e,e') -> notImpl expr
        | DefaultValue(t) -> notImpl expr
        | Quote e -> raise <| new NotSupportedException("nested quotations not supported")
        | _ -> notImpl expr

    let synExprToLetBinding (expr : SynExpr) =
        let synPat = SynPat.LongIdent(mkLongIdent range0 [mkIdent range0 compiledFunctionName], None, None, SynConstructorArgs.Pats [ SynPat.Paren(SynPat.Const(SynConst.Unit, range0), range0)], None, range0)
        let binding = mkBinding range0 synPat expr
        SynModuleDecl.Let(false, [binding], range0)

    let letBindingToParsedInput (decl : SynModuleDecl) =
        let modl = SynModuleOrNamespace([mkIdent range0 moduleName], true, [decl], PreXmlDoc.Empty,[], None, range0)
        let file = ParsedImplFileInput("/QuotationCompiler.fs", false, QualifiedNameOfFile(mkIdent range0 moduleName), [],[], [modl],false)
        ParsedInput.ImplFile file

    let synExpr = exprToAst expr
    let parsedInput = synExpr |> synExprToLetBinding |> letBindingToParsedInput
    let assemblies = dependencies.Value.Assemblies
    assemblies, parsedInput