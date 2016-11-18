module Fable.FSharp2Fable.Compiler

open System.IO
open System.Collections.Generic
open System.Text.RegularExpressions

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices

open Fable
open Fable.AST
open Fable.AST.Fable.Util

open Patterns
open Types
open Identifiers
open Helpers
open Util

// Special values like seq, async, String.Empty...
let private (|SpecialValue|_|) com ctx = function
    | BasicPatterns.ILFieldGet (None, typ, fieldName) as fsExpr when typ.HasTypeDefinition ->
        match typ.TypeDefinition.TryFullName, fieldName with
        | Some "System.String", "Empty" -> Some (makeConst "")
        | Some "System.Guid", "Empty" -> Some (makeConst "00000000-0000-0000-0000-000000000000")
        | Some "System.TimeSpan", "Zero" ->
            Fable.Wrapped(makeConst 0, makeType com ctx fsExpr.Type) |> Some
        | Some "System.DateTime", "MaxValue"
        | Some "System.DateTime", "MinValue" ->
            CoreLibCall("Date", Some (Naming.lowerFirst fieldName), false, [])
            |> makeCall (makeRangeFrom fsExpr) (makeType com ctx fsExpr.Type) |> Some
        | _ -> None
    | _ -> None

let private (|BaseCons|_|) com ctx = function
    | BasicPatterns.NewObject(meth, _, args) ->
        match ctx.baseClass with
        | Some baseFullName
            when sanitizeEntityFullName meth.EnclosingEntity = baseFullName ->
            Some (meth, args)
        | _ -> None
    | BasicPatterns.Call(None, meth, _, _, args) as fsExpr ->
        match ctx.baseClass with
        | Some baseFullName when meth.CompiledName = ".ctor"
                            && (sanitizeEntityFullName meth.EnclosingEntity) = baseFullName ->
            if not meth.IsImplicitConstructor then
                FableError("Inheritance is only possible with base class primary constructor: "
                            + baseFullName, makeRange fsExpr.Range) |> raise
            Some (meth, args)
        | _ -> None
    | _ -> None

let rec private transformNewList com ctx (fsExpr: FSharpExpr) fsType argExprs =
    let rec flattenList (r: SourceLocation) accArgs = function
        | [] -> accArgs, None
        | arg::[BasicPatterns.NewUnionCase(_, _, rest)] ->
            flattenList r (arg::accArgs) rest
        | arg::[baseList] ->
            arg::accArgs, Some baseList
        | _ -> failwithf "Unexpected List constructor %O: %A" r fsExpr
    let isKeyValueList (fsType: FSharpType) =
        match Seq.toList fsType.GenericArguments with
        | [arg] when arg.HasTypeDefinition ->
            arg.TypeDefinition.Attributes |> hasAtt Atts.keyValueList
        | _ -> false
    let unionType, range = makeType com ctx fsType, makeRange fsExpr.Range
    if isKeyValueList fsType then
        let (|KeyValue|_|) = function
            | Fable.Value(Fable.TupleConst([Fable.Value(Fable.StringConst k);v])) -> Some(k, v)
            | _ -> None
        match flattenList range [] argExprs with
        | _, Some baseList ->
            FableError("KeyValue lists cannot be composed", range) |> raise
        | args, None ->
            (Some [], args) ||> List.fold (fun acc x ->
                match acc, transformExpr com ctx x with
                | Some acc, Fable.Wrapped(KeyValue(k,v),_)
                | Some acc, KeyValue(k,v) -> (k,v)::acc |> Some
                | None, _ -> None // If a case cannot be determined at compile time
                | _ -> None       // the whole list must be converted at runtime
            ) |> function
            | Some cases -> makeJsObject (Some range) cases
            | None ->
                let args =
                    let args = args |> List.map (transformExpr com ctx)
                    Fable.Value (Fable.ArrayConst (Fable.ArrayValues args, Fable.Any))
                let builder =
                    Fable.Emit("(o, kv) => { o[kv[0]] = kv[1]; return o; }") |> Fable.Value
                CoreLibCall("Seq", Some "fold", false, [builder;Fable.ObjExpr([],[],None,None);args])
                |> makeCall (Some range) Fable.Any
    else
        let buildArgs (args, baseList) =
            let args = args |> List.rev |> (List.map (transformExpr com ctx))
            let ar = Fable.Value (Fable.ArrayConst (Fable.ArrayValues args, Fable.Any))
            ar::(match baseList with Some li -> [transformExpr com ctx li] | None -> [])
        match argExprs with
        | [] -> CoreLibCall("List", None, true, [])
        | _ ->
            match flattenList range [] argExprs with
            | [arg], Some baseList ->
                let args = List.map (transformExpr com ctx) [arg; baseList]
                CoreLibCall("List", None, true, args)
            | args, baseList ->
                let args = buildArgs(args, baseList)
                CoreLibCall("List", Some "ofArray", false, args)
        |> makeCall (Some range) unionType

and private transformNonListNewUnionCase com ctx (fsExpr: FSharpExpr) fsType unionCase argExprs =
    let unionType, range = makeType com ctx fsType, makeRange fsExpr.Range
    match unionType with
    | OptionUnion ->
        match argExprs: Fable.Expr list with
        // Represent `Some ()` with an empty object, see #478
        | expr::_ when expr.Type = Fable.Unit ->
            Fable.Wrapped(Fable.ObjExpr([], [], None, Some range), unionType)
        | expr::_ -> Fable.Wrapped(expr, unionType)
        | _ -> Fable.Wrapped(Fable.Value Fable.Null, unionType)
    | ErasedUnion ->
        match argExprs with
        | [] -> Fable.Wrapped(Fable.Value Fable.Null, unionType)
        | [expr] -> Fable.Wrapped(expr, unionType)
        | _ -> FableError("Erased Union Cases must have one single field: " + unionType.FullName, range) |> raise
    | KeyValueUnion ->
        let key, value =
            match argExprs with
            | [] -> lowerCaseName unionCase, makeConst true
            | [expr] -> lowerCaseName unionCase, expr
            | [key; expr] when hasAtt Atts.erase unionCase.Attributes -> key, expr
            | _ -> FableError("KeyValue Union Cases must have one or zero fields: " + unionType.FullName, range) |> raise
        Fable.TupleConst [key; value] |> Fable.Value
    | StringEnum ->
        // if argExprs.Length > 0 then
        //     failwithf "StringEnum must not have fields: %s" unionType.FullName
        lowerCaseName unionCase
    | ListUnion ->
        failwithf "transformNonListNewUnionCase must not be used with List %O" range
    | OtherType ->
        let argExprs = [
            makeConst unionCase.Name    // Include Tag name in args
            Fable.Value(Fable.ArrayConst(Fable.ArrayValues argExprs, Fable.Any))
        ]
        buildApplyInfo com ctx (Some range) unionType unionType (unionType.FullName)
            ".ctor" Fable.Constructor ([],[],[],0) (None, argExprs)
        |> tryReplace com (tryDefinition fsType)
        |> function
        | Some repl -> repl
        | None -> Fable.Apply(makeNonGenTypeRef unionType, argExprs, Fable.ApplyCons, unionType, Some range)

and private transformComposableExpr com ctx fsExpr argExprs =
    // See (|ComposableExpr|_|) active pattern to check which expressions are valid here
    match fsExpr with
    | BasicPatterns.Call(None, meth, typArgs, methTypArgs, _) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, methTypArgs) None argExprs
    | BasicPatterns.NewObject(meth, typArgs, _) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, []) None argExprs
    | BasicPatterns.NewUnionCase(fsType, unionCase, _) ->
        transformNonListNewUnionCase com ctx fsExpr fsType unionCase argExprs
    | _ -> failwithf "Expected ComposableExpr %O" (makeRange fsExpr.Range)

and private transformExpr (com: IFableCompiler) ctx fsExpr =
    transformExprWithRole UnknownRole com ctx fsExpr

and private transformExprWithRole (role: Role) (com: IFableCompiler) ctx fsExpr =
    match fsExpr with
    (** ## Custom patterns *)
    | SpecialValue com ctx replacement ->
        replacement

    // TODO: Detect if it's ResizeArray and compile as FastIntegerForLoop?
    | ForOf (BindIdent com ctx (newContext, ident), Transform com ctx value, body) ->
        Fable.ForOf (ident, value, transformExpr com newContext body)
        |> makeLoop (makeRangeFrom fsExpr)

    | ErasableLambda (expr, argExprs) ->
        List.map (transformExprWithRole AppliedArgument com ctx) argExprs
        |> transformComposableExpr com ctx expr

    // Pipe must come after ErasableLambda
    | Pipe (Transform com ctx callee, args) ->
        let typ, range = makeType com ctx fsExpr.Type, makeRangeFrom fsExpr
        makeApply range typ callee (List.map (transformExprWithRole AppliedArgument com ctx) args)

    | Composition (expr1, args1, expr2, args2) ->
        let lambdaArg = com.GetUniqueVar() |> makeIdent
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        let expr1 =
            (List.map (transformExprWithRole AppliedArgument com ctx) args1)
                @ [Fable.Value (Fable.IdentValue lambdaArg)]
            |> transformComposableExpr com ctx expr1
        let expr2 =
            (List.map (transformExprWithRole AppliedArgument com ctx) args2)@[expr1]
            |> transformComposableExpr com ctx expr2
        makeLambdaExpr [lambdaArg] expr2

    | BaseCons com ctx (meth, args) ->
        if Naming.ignoredBaseClasses |> Seq.exists meth.FullName.StartsWith
        then Fable.Value Fable.Null
        else
            let args = List.map (transformExprWithRole AppliedArgument com ctx) args
            let typ, range = makeType com ctx fsExpr.Type, makeRangeFrom fsExpr
            Fable.Apply(Fable.Value Fable.Super, args, Fable.ApplyMeth, typ, range)

    | TryGetValue (callee, meth, typArgs, methTypArgs, methArgs) ->
        let callee, args = Option.map (com.Transform ctx) callee, List.map (com.Transform ctx) methArgs
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, methTypArgs) callee args

    | CreateEvent (callee, eventName, meth, typArgs, methTypArgs, methArgs) ->
        let callee, args = com.Transform ctx callee, List.map (com.Transform ctx) methArgs
        let callee = Fable.Apply(callee, [makeConst eventName], Fable.ApplyGet, Fable.Any, None)
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, methTypArgs) (Some callee) args

    | CheckArrayLength (Transform com ctx arr, length) ->
        let r = makeRangeFrom fsExpr
        let lengthExpr = Fable.Apply(arr, [makeConst "length"], Fable.ApplyGet, Fable.Number Int32, r)
        makeEqOp r [lengthExpr; makeConst length] BinaryEqualStrict

    | PrintFormat (Transform com ctx expr) -> expr

    | Applicable (Transform com ctx expr) ->
        let appType =
            let ent = Fable.Entity(lazy Fable.Interface, None, "Fable.Core.Applicable", lazy [])
            Fable.DeclaredType(ent, [Fable.Any; Fable.Any])
        Fable.Wrapped(expr, appType)

    | RecordMutatingUpdate(NonAbbreviatedType fsType, record, updatedFields) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsType
        // TODO: Use a different role type?
        let record = makeValueFrom com ctx r typ AppliedArgument record
        let assignments =
            ([record], updatedFields)
            ||> List.fold (fun acc (FieldName fieldName, e) ->
                let r, value = makeRangeFrom e, com.Transform ctx e
                let e = Fable.Set(record, Some(makeConst fieldName), value, r)
                e::acc)
        Fable.Sequential(assignments, r)

    | JsFunc(thisVar, lambda) ->
        let thisType = makeType com ctx thisVar.FullType
        let ctx, ident = bindIdent com ctx thisType (Some thisVar) thisVar.CompiledName
        com.Transform ctx lambda
        |> makeDelegate com None
        |> function
            | Fable.Value(Fable.Lambda(args,body,_)) ->
                // Make a explicit reference to `this` to prevent an inner lambda
                // tries to capture the enclosing `this`.
                let assignment = Fable.VarDeclaration (ident, Fable.Value Fable.This, false)
                let body = makeSequential body.Range [assignment; body]
                Fable.Value(Fable.Lambda(args,body,false))
            | e -> e // TODO: failwith "unexpected"?

    (** ## Erased *)
    | BasicPatterns.Coerce(_targetType, Transform com ctx inpExpr) -> inpExpr
    // TypeLambda is a local generic lambda
    // e.g, member x.Test() = let typeLambda x = x in typeLambda 1, typeLambda "A"
    // Sometimes these must be inlined, but that's resolved in BasicPatterns.Let (see below)
    | BasicPatterns.TypeLambda (_genArgs, Transform com ctx lambda) -> lambda

    (** ## Flow control *)
    | BasicPatterns.FastIntegerForLoop(Transform com ctx start, Transform com ctx limit, body, isUp) ->
        match body with
        | BasicPatterns.Lambda (BindIdent com ctx (newContext, ident), body) ->
            Fable.For (ident, start, limit, com.Transform newContext body, isUp)
            |> makeLoop (makeRangeFrom fsExpr)
        | _ -> failwithf "Unexpected loop %O: %A" (makeRange fsExpr.Range) fsExpr

    | BasicPatterns.WhileLoop(Transform com ctx guardExpr, Transform com ctx bodyExpr) ->
        Fable.While (guardExpr, bodyExpr)
        |> makeLoop (makeRangeFrom fsExpr)

    (** Values *)
    // Arrays with small data (ushort, byte) won't fit the NewArray pattern
    // as they would require too much memory
    | BasicPatterns.Const(:? System.Array as arr, typ) ->
        let arrExprs = [
            for i in 0 .. (arr.GetLength(0) - 1) ->
                arr.GetValue(i) |> makeConst
        ]
        match arr.GetType().GetElementType().FullName with
        | NumberKind kind -> Fable.Number kind
        | _ -> Fable.Any
        |> makeArray <| arrExprs

    | BasicPatterns.Const(value, FableType com ctx typ) ->
        let e = makeConst value
        if e.Type = typ then e
        // Enumerations are compiled as const but they have a different type
        else Fable.Wrapped (e, typ)

    | BasicPatterns.BaseValue typ ->
        Fable.Super |> Fable.Value

    | BasicPatterns.ThisValue _typ ->
        makeThisRef ctx None

    | BasicPatterns.Value v when v.IsMemberThisValue ->
        Some v |> makeThisRef ctx

    | BasicPatterns.Value v ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeValueFrom com ctx r typ role v

    | BasicPatterns.DefaultValue (FableType com ctx typ) ->
        let valueKind =
            match typ with
            | Fable.Boolean -> Fable.BoolConst false
            | Fable.Number kind -> Fable.NumberConst (U2.Case1 0, kind)
            | _ -> Fable.Null
        Fable.Value valueKind

    (** ## Assignments *)
    | ImmutableBinding((var, value), body) ->
        transformExpr com ctx value |> bindExpr ctx var |> transformExpr com <| body

    | BasicPatterns.Let((var, value), body) ->
        if isInline var then
            let ctx = { ctx with scopedInlines = (var, value)::ctx.scopedInlines }
            transformExpr com ctx body
        else
            let value = transformExpr com ctx value
            let ctx, ident = bindIdent com ctx value.Type (Some var) var.CompiledName
            let body = transformExpr com ctx body
            let assignment = Fable.VarDeclaration (ident, value, var.IsMutable)
            makeSequential (makeRangeFrom fsExpr) [assignment; body]

    | BasicPatterns.LetRec(recBindings, body) ->
        let ctx, idents =
            (recBindings, (ctx, [])) ||> List.foldBack (fun (var,_) (ctx, idents) ->
                let (BindIdent com ctx (newContext, ident)) = var
                (newContext, ident::idents))
        let assignments =
            recBindings
            |> List.map2 (fun ident (var, Transform com ctx binding) ->
                Fable.VarDeclaration (ident, binding, var.IsMutable)) idents
        assignments @ [transformExpr com ctx body]
        |> makeSequential (makeRangeFrom fsExpr)

    (** ## Applications *)
    | BasicPatterns.TraitCall(sourceTypes, traitName, flags, argTypes, _argTypes2, argExprs) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        let giveUp() =
            FableError("Cannot resolve trait call " + traitName, ?range=r) |> raise
        let (|ResolveGeneric|_|) ctx (t: FSharpType) =
            if not t.IsGenericParameter then Some t else
            let genParam = t.GenericParameter
            ctx.typeArgs |> List.tryPick (fun (name,t) ->
                if name = genParam.Name then Some t else None)
        let makeCall meth =
            let callee, args =
                if flags.IsInstance
                then
                    (transformExpr com ctx argExprs.Head |> Some),
                    (List.map (transformExprWithRole AppliedArgument com ctx) argExprs.Tail)
                else None, List.map (transformExprWithRole AppliedArgument com ctx) argExprs
            makeCallFrom com ctx r typ meth ([],[]) callee args
        sourceTypes
        |> List.tryPick (function
            | ResolveGeneric ctx (TypeDefinition tdef) ->
                tdef.MembersFunctionsAndValues |> Seq.filter (fun m ->
                    // Property members that are no getter nor setter don't actually get implemented
                    not(m.IsProperty && not(m.IsPropertyGetterMethod || m.IsPropertySetterMethod))
                    && m.IsInstanceMember = flags.IsInstance
                    && m.CompiledName = traitName)
                |> Seq.toList |> function [] -> None | ms -> Some (tdef, ms)
            | _ -> None)
        |> function
        | Some(_, [meth]) ->
            makeCall meth
        | Some(tdef, candidates) ->
            let argTypes =
                if not flags.IsInstance then argTypes else argTypes.Tail
                |> List.map (makeType com ctx)
            candidates |> List.tryPick (fun meth ->
                let methTypes = getArgTypes com meth.CurriedParameterGroups
                if compareConcreteAndGenericTypes argTypes methTypes
                then Some meth else None)
            |> function Some m -> makeCall m | None -> giveUp()
        | None -> giveUp()

    | BasicPatterns.Call(callee, meth, typArgs, methTypArgs, args) ->
        let callee = Option.map (com.Transform ctx) callee
        let args = List.map (transformExprWithRole AppliedArgument com ctx) args
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, methTypArgs) callee args

    // Application of locally inlined lambdas
    | BasicPatterns.Application(BasicPatterns.Value var, typeArgs, args) when isInline var ->
        let range = makeRange fsExpr.Range
        match ctx.scopedInlines |> List.tryFind (fun (v,_) -> obj.Equals(v, var)) with
        | Some (_,fsExpr) ->
            let typ = makeType com ctx fsExpr.Type
            let args = List.map (transformExprWithRole AppliedArgument com ctx) args
            let resolvedCtx = { ctx with typeArgs = matchGenericParams com ctx var ([], typeArgs) }
            let callee = com.Transform resolvedCtx fsExpr
            makeApply (Some range) typ callee args
        | None ->
            FableError("Cannot resolve locally inlined value: " + var.DisplayName, range) |> raise

    | BasicPatterns.Application(Transform com ctx callee, _typeArgs, args) ->
        let args = List.map (transformExprWithRole AppliedArgument com ctx) args
        let typ, range = makeType com ctx fsExpr.Type, makeRangeFrom fsExpr
        if callee.Type.FullName = "Fable.Core.Applicable" then
            match args with
            | [Fable.Value(Fable.TupleConst args)] -> args
            | args -> args
            |> List.map (makeDelegate com None)
            |> fun args -> Fable.Apply(callee, args, Fable.ApplyMeth, typ, range)
        else makeApply range typ callee args

    | BasicPatterns.IfThenElse (Transform com ctx guardExpr, Transform com ctx thenExpr, Transform com ctx elseExpr) ->
        Fable.IfThenElse (guardExpr, thenExpr, elseExpr, makeRangeFrom fsExpr)

    | BasicPatterns.TryFinally (BasicPatterns.TryWith(body, _, _, catchVar, catchBody),finalBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) (Some finalBody)

    | BasicPatterns.TryFinally (body, finalBody) ->
        makeTryCatch com ctx fsExpr body None (Some finalBody)

    | BasicPatterns.TryWith (body, _, _, catchVar, catchBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) None

    | BasicPatterns.Sequential (Transform com ctx first, Transform com ctx second) ->
        makeSequential (makeRangeFrom fsExpr) [first; second]

    (** ## Lambdas *)
    | BasicPatterns.Lambda (var, body) ->
        let ctx, args = makeLambdaArgs com ctx [var]
        Fable.Lambda (args, transformExpr com ctx body, true) |> Fable.Value

    | BasicPatterns.NewDelegate(_delegateType, Transform com ctx delegateBodyExpr) ->
        makeDelegate com None delegateBodyExpr

    (** ## Getters and Setters *)
    | BasicPatterns.FSharpFieldGet (callee, calleeType, FieldName fieldName) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeType com ctx calleeType
                      |> makeNonGenTypeRef
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeGetFrom com ctx r typ callee (makeConst fieldName)

    | BasicPatterns.TupleGet (_tupleType, tupleElemIndex, Transform com ctx tupleExpr) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeGetFrom com ctx r typ tupleExpr (makeConst tupleElemIndex)

    | BasicPatterns.UnionCaseGet (Transform com ctx unionExpr, FableType com ctx unionType, unionCase, FieldName fieldName) ->
        let typ, range = makeType com ctx fsExpr.Type, makeRangeFrom fsExpr
        match unionType with
        | ErasedUnion | OptionUnion ->
            Fable.Wrapped(unionExpr, typ)
        | ListUnion ->
            makeGet range typ unionExpr (Naming.lowerFirst fieldName |> makeConst)
        | _ ->
            let i = unionCase.UnionCaseFields |> Seq.findIndex (fun x -> x.Name = fieldName)
            let fields = makeGet range typ unionExpr ("Fields" |> makeConst)
            makeGet range typ fields (i |> makeConst)

    | BasicPatterns.ILFieldSet (callee, typ, fieldName, value) ->
        failwithf "Unsupported ILField reference %O: %A" (makeRange fsExpr.Range) fsExpr

    | BasicPatterns.FSharpFieldSet (callee, FableType com ctx calleeType, FieldName fieldName, Transform com ctx value) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeNonGenTypeRef calleeType
        Fable.Set (callee, Some (makeConst fieldName), value, makeRangeFrom fsExpr)

    | BasicPatterns.UnionCaseTag (Transform com ctx unionExpr, _unionType) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeGetFrom com ctx r typ unionExpr (makeConst "tag")

    | BasicPatterns.UnionCaseSet (Transform com ctx unionExpr, _type, _case, _caseField, _valueExpr) ->
        makeRange fsExpr.Range |> failwithf "Unexpected UnionCaseSet %O"

    | BasicPatterns.ValueSet (valToSet, Transform com ctx valueExpr) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx valToSet.FullType
        let valToSet = makeValueFrom com ctx r typ UnknownRole valToSet
        Fable.Set (valToSet, None, valueExpr, r)

    (** Instantiation *)
    | BasicPatterns.NewArray(FableType com ctx elTyp, arrExprs) ->
        makeArray elTyp (arrExprs |> List.map (transformExpr com ctx))

    | BasicPatterns.NewTuple(_, argExprs) ->
        argExprs |> List.map (transformExpr com ctx) |> Fable.TupleConst |> Fable.Value

    | BasicPatterns.ObjectExpr(objType, baseCallExpr, overrides, otherOverrides) ->
        // If `this` is available, capture it to avoid conflicts (see #158)
        let capturedThis =
            match ctx.thisAvailability with
            | ThisUnavailable -> None
            | ThisAvailable -> Some [None, com.GetUniqueVar() |> makeIdent]
            | ThisCaptured(prevThis, prevVars) ->
                (Some prevThis, com.GetUniqueVar() |> makeIdent)::prevVars |> Some
        let baseClass, baseCons =
            match baseCallExpr with
            | BasicPatterns.Call(None, meth, _, _, args)
                    when Naming.ignoredBaseClasses |> Seq.exists meth.FullName.StartsWith |> not ->
                let args = List.map (com.Transform ctx) args
                let typ, range = makeType com ctx baseCallExpr.Type, makeRange baseCallExpr.Range
                let baseClass =
                    makeTypeFromDef com ctx meth.EnclosingEntity []
                    |> makeNonGenTypeRef |> Some
                let baseCons =
                    let c = Fable.Apply(Fable.Value Fable.Super, args, Fable.ApplyMeth, typ, Some range)
                    let m = Fable.Member(".ctor", Fable.Constructor, Fable.InstanceLoc, [], Fable.Any)
                    Some(m, [], c)
                baseClass, baseCons
            | _ -> None, None
        let members =
            (objType, overrides)::otherOverrides
            |> List.collect (fun (typ, overrides) ->
                overrides |> List.map (fun over ->
                    let info = { isInstance = true; passGenerics = false }
                    let args, range = over.CurriedParameterGroups, makeRange fsExpr.Range
                    let ctx, thisArg, args' = bindMemberArgs com ctx info args
                    let ctx =
                        match capturedThis, thisArg with
                        | None, _ -> ctx
                        | Some(capturedThis), Some thisArg ->
                            { ctx with thisAvailability=ThisCaptured(thisArg, capturedThis) }
                        | Some _, None -> failwithf "Unexpected Object Expression method withouth `this` argument %O" range
                    // Don't use the typ argument as the override may come
                    // from another type, like ToString()
                    let typ =
                        if over.Signature.DeclaringType.HasTypeDefinition
                        then Some over.Signature.DeclaringType.TypeDefinition
                        else None
                    // TODO: Check for indexed getter and setter also in object expressions?
                    let name = over.Signature.Name |> Naming.removeGetSetPrefix
                    let kind =
                        match over.Signature.Name with
                        | Naming.StartsWith "get_" _ -> Fable.Getter
                        | Naming.StartsWith "set_" _ -> Fable.Setter
                        | _ -> Fable.Method
                    // FSharpObjectExprOverride.CurriedParameterGroups doesn't offer
                    // information about ParamArray, we need to check the source method.
                    let hasRestParams =
                        match typ with
                        | None -> false
                        | Some typ ->
                            typ.MembersFunctionsAndValues
                            |> Seq.tryFind (fun x -> x.CompiledName = over.Signature.Name)
                            |> function Some m -> hasRestParams m | None -> false
                    let body = transformExpr com ctx over.Body
                    let args = List.map Fable.Ident.getType args'
                    let m = Fable.Member(name, kind, Fable.InstanceLoc, args, body.Type, Fable.Function(args, body.Type),
                                over.GenericParameters |> List.map (fun x -> x.Name),
                                hasRestParams = hasRestParams)
                    m, args', body))
        let members =
            match baseCons with
            | Some baseCons -> baseCons::members
            | None -> members
        let interfaces =
            objType::(otherOverrides |> List.map fst)
            |> List.map (fun x -> sanitizeEntityFullName x.TypeDefinition)
            |> List.distinct
        let range = makeRangeFrom fsExpr
        let objExpr = Fable.ObjExpr (members, interfaces, baseClass, range)
        match capturedThis with
        | Some((_,capturedThis)::_) ->
            let varDecl = Fable.VarDeclaration(capturedThis, Fable.Value Fable.This, false)
            Fable.Sequential([varDecl; objExpr], range)
        | _ -> objExpr

    | BasicPatterns.NewObject(meth, typArgs, args) ->
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        makeCallFrom com ctx r typ meth (typArgs, []) None (List.map (com.Transform ctx) args)

    | BasicPatterns.NewRecord(NonAbbreviatedType fsType, argExprs) ->
        let recordType, range = makeType com ctx fsType, makeRange fsExpr.Range
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        buildApplyInfo com ctx (Some range) recordType recordType (recordType.FullName)
            ".ctor" Fable.Constructor ([],[],[],0) (None, argExprs)
        |> tryReplace com (tryDefinition fsType)
        |> function
        | Some repl -> repl
        | None -> Fable.Apply(makeNonGenTypeRef recordType, argExprs, Fable.ApplyCons,
                        makeType com ctx fsExpr.Type, Some range)

    | BasicPatterns.NewUnionCase(NonAbbreviatedType fsType, unionCase, argExprs) ->
        match fsType with
        | ListType _ -> transformNewList com ctx fsExpr fsType argExprs
        | _ ->
            List.map (com.Transform ctx) argExprs
            |> transformNonListNewUnionCase com ctx fsExpr fsType unionCase

    (** ## Type test *)
    | BasicPatterns.TypeTest (FableType com ctx typ as fsTyp, Transform com ctx expr) ->
        makeTypeTest (makeRangeFrom fsExpr) typ expr

    | BasicPatterns.UnionCaseTest(Transform com ctx unionExpr,
                                  (FableType com ctx unionType as fsType),
                                  unionCase) ->
        match unionType with
        | ErasedUnion ->
            if unionCase.UnionCaseFields.Count <> 1 then
                FableError("Erased Union Cases must have one single field: "
                            + unionType.FullName, makeRange fsExpr.Range) |> raise
            else
                let typ =
                    let m = Regex.Match(unionCase.Name, @"^Case(\d+)$")
                    if m.Success
                    then
                        let idx = int m.Groups.[1].Value - 1
                        if fsType.GenericArguments.Count > idx
                        then makeType com ctx fsType.GenericArguments.[idx]
                        else unionType
                    else unionType
                makeTypeTest (makeRangeFrom fsExpr) typ unionExpr
        | OptionUnion ->
            let opKind = if unionCase.Name = "None" then BinaryEqual else BinaryUnequal
            makeBinOp (makeRangeFrom fsExpr) Fable.Boolean [unionExpr; Fable.Value Fable.Null] opKind
        | ListUnion ->
            let opKind = if unionCase.CompiledName = "Empty" then BinaryEqual else BinaryUnequal
            let expr = makeGet None Fable.Any unionExpr (makeConst "tail")
            makeBinOp (makeRangeFrom fsExpr) Fable.Boolean [expr; Fable.Value Fable.Null] opKind
        | StringEnum ->
            makeBinOp (makeRangeFrom fsExpr) Fable.Boolean [unionExpr; lowerCaseName unionCase] BinaryEqualStrict
        | _ ->
            let left = makeGet None Fable.String unionExpr (makeConst "Case")
            let right = makeConst unionCase.Name
            makeBinOp (makeRangeFrom fsExpr) Fable.Boolean [left; right] BinaryEqualStrict

    (** Pattern Matching *)
    | Switch(matchValue, cases, defaultCase, decisionTargets) ->
        let transformCases assignVar =
            let transformBody idx =
                let body = transformExpr com ctx (snd decisionTargets.[idx])
                match assignVar with
                | Some assignVar -> Fable.Set(assignVar, None, body, body.Range)
                | None -> body
            let cases =
                cases |> Seq.map (fun kv ->
                    List.map makeConst kv.Value, transformBody kv.Key)
                |> Seq.toList
            let defaultCase = transformBody defaultCase
            cases, defaultCase
        let matchValue =
            let t = makeType com ctx matchValue.FullType
            makeValueFrom com ctx None t UnknownRole matchValue
        let r, typ = makeRangeFrom fsExpr, makeType com ctx fsExpr.Type
        match typ with
        | Fable.Unit ->
            let cases, defaultCase = transformCases None
            Fable.Switch(matchValue, cases, Some defaultCase, typ, r)
        | _ ->
            let assignVar = com.GetUniqueVar() |> makeIdent
            let cases, defaultCase =
                Fable.IdentValue assignVar |> Fable.Value |> Some |> transformCases
            makeSequential r [
                Fable.VarDeclaration(assignVar, Fable.Value Fable.Null, true)
                Fable.Switch(matchValue, cases, Some defaultCase, typ, r)
                Fable.Value(Fable.IdentValue assignVar)
            ]

    | BasicPatterns.DecisionTree(decisionExpr, decisionTargets) ->
        let rec getTargetRefsCount map = function
            | BasicPatterns.IfThenElse (_, thenExpr, elseExpr)
            | BasicPatterns.Let(_, BasicPatterns.IfThenElse (_, thenExpr, elseExpr)) ->
                let map = getTargetRefsCount map thenExpr
                getTargetRefsCount map elseExpr
            | BasicPatterns.Let(_, e) ->
                getTargetRefsCount map e
            | BasicPatterns.DecisionTreeSuccess (idx, _) ->
                match Map.tryFind idx map with
                | Some refCount -> Map.add idx (refCount + 1) map
                | None -> Map.add idx 1 map
            | e ->
                failwithf "Unexpected DecisionTree branch %O: %A" (makeRange e.Range) e
        let targetRefsCount = getTargetRefsCount (Map.empty<int,int>) decisionExpr
        // Convert targets referred more than once into functions
        // and just pass the F# implementation for the others
        let ctx, assignments =
            targetRefsCount
            |> Map.filter (fun k v -> v > 1)
            |> Map.fold (fun (ctx, acc) k v ->
                let targetVars, targetExpr = decisionTargets.[k]
                let targetVars, targetCtx =
                    (targetVars, ([], ctx)) ||> List.foldBack (fun var (vars, ctx) ->
                        let ctx, var = bindIdentFrom com ctx var
                        var::vars, ctx)
                let lambda =
                    com.Transform targetCtx targetExpr |> makeLambdaExpr targetVars
                let ctx, ident = bindIdent com ctx lambda.Type None (sprintf "$target%i" k)
                ctx, Map.add k (ident, lambda) acc) (ctx, Map.empty<_,_>)
        let decisionTargets =
            targetRefsCount |> Map.map (fun k v ->
                match v with
                | 1 -> TargetImpl decisionTargets.[k]
                | _ -> TargetRef (fst assignments.[k]))
        let ctx = { ctx with decisionTargets = decisionTargets }
        if assignments.Count = 0 then
            transformExpr com ctx decisionExpr
        else
            let assignments =
                assignments
                |> Seq.map (fun pair ->
                    let ident, lambda = pair.Value
                    Fable.VarDeclaration (ident, lambda, false))
                |> Seq.toList
            Fable.Sequential (assignments @ [transformExpr com ctx decisionExpr], makeRangeFrom fsExpr)

    | BasicPatterns.DecisionTreeSuccess (decIndex, decBindings) ->
        match Map.tryFind decIndex ctx.decisionTargets with
        | None -> failwithf "Missing decision target %O" (makeRange fsExpr.Range)
        // If we get a reference to a function, call it
        | Some (TargetRef targetRef) ->
            Fable.Apply (Fable.IdentValue targetRef |> Fable.Value,
                (decBindings |> List.map (transformExpr com ctx)),
                Fable.ApplyMeth, makeType com ctx fsExpr.Type, makeRangeFrom fsExpr)
        // If we get an implementation without bindings, just transform it
        | Some (TargetImpl ([], Transform com ctx decBody)) -> decBody
        // If we have bindings, create the assignments
        | Some (TargetImpl (decVars, decBody)) ->
            let newContext, assignments =
                List.foldBack2 (fun var (Transform com ctx binding) (accContext, accAssignments) ->
                    let (BindIdent com accContext (newContext, ident)) = var
                    let assignment = Fable.VarDeclaration (ident, binding, var.IsMutable)
                    newContext, (assignment::accAssignments)) decVars decBindings (ctx, [])
            assignments @ [transformExpr com newContext decBody]
            |> makeSequential (makeRangeFrom fsExpr)

    | BasicPatterns.Quote(Transform com ctx expr) ->
        Fable.Quote(expr)

    (** Not implemented *)
    | BasicPatterns.ILAsm _
    | BasicPatterns.ILFieldGet _
    | BasicPatterns.AddressOf _ // (lvalueExpr)
    | BasicPatterns.AddressSet _ // (lvalueExpr, rvalueExpr)
    | _ -> failwithf "Cannot compile expression in %O: %A"
                     (makeRange fsExpr.Range) fsExpr

let private processMemberDecls (com: IFableCompiler) ctx (fableEnt: Fable.Entity) (childDecls: #seq<Fable.Declaration>) =
    if fableEnt.Kind = Fable.Module then Seq.toList childDecls else
    // If F# union or records implement System.IComparable/System.Equatable generate the methods
    // Note: F# compiler generates these methods too but see `IsIgnoredMethod`
    let needsEqImpl =
        fableEnt.HasInterface "System.IEquatable"
        && fableEnt.TryGetDecorator("Microsoft.FSharp.Core.CustomEquality").IsNone
    let needsCompImpl =
        fableEnt.HasInterface "System.IComparable"
        && fableEnt.TryGetDecorator("Microsoft.FSharp.Core.CustomComparison").IsNone
    let fableType =
        Fable.DeclaredType(fableEnt, fableEnt.GenericParameters |> List.map Fable.GenericParam)
    // Unions, records and F# exceptions don't have a constructor
    match fableEnt.Kind with
    | Fable.Union cases ->
      [ yield makeUnionCons()
        yield makeTypeNameMeth fableEnt.FullName
        yield makeInterfacesMethod fableEnt
                false ("FSharpUnion"::fableEnt.Interfaces)
        yield makeCasesMethod cases
        if needsEqImpl then yield makeUnionEqualMethod fableType
        if needsCompImpl then yield makeUnionCompareMethod fableType ]
    // TODO: Use specific interface for FSharpException?
    | Fable.Record fields
    | Fable.Exception fields ->
      [ yield makeRecordCons fields
        yield makeTypeNameMeth fableEnt.FullName
        yield makeInterfacesMethod fableEnt
                false ("FSharpRecord"::fableEnt.Interfaces)
        yield makePropertiesMethod fableEnt false fields
        if needsEqImpl then yield makeRecordEqualMethod fableType
        if needsCompImpl then yield makeRecordCompareMethod fableType ]
    | Fable.Class(baseClass, properties) ->
      [ yield makeTypeNameMeth fableEnt.FullName
        if baseClass.IsSome || fableEnt.Interfaces.Length > 0 then
            yield makeInterfacesMethod fableEnt baseClass.IsSome fableEnt.Interfaces
        yield makePropertiesMethod fableEnt baseClass.IsSome properties ]
    | _ -> []
    |> fun autoMeths -> [yield! autoMeths; yield! childDecls]

// The F# compiler considers class methods as children of the enclosing module.
// We use this type to correct that, see type DeclInfo below.
type private TmpDecl =
    | Decl of Fable.Declaration
    | Ent of Fable.Entity * string * ResizeArray<Fable.Declaration> * SourceLocation
    | IgnoredEnt

type private DeclInfo() =
    let publicNames = ResizeArray<string>()
    // Check there're no conflicting entity or function names (see #166)
    let checkPublicNameConflicts name =
        if publicNames.Contains name then
            FableError("Public types, modules or functions with same name "
                        + "at same level are not supported: " + name) |> raise
        publicNames.Add name
    let decls = ResizeArray<_>()
    let children = Dictionary<string, TmpDecl>()
    let tryFindChild (ent: FSharpEntity) =
        if children.ContainsKey ent.FullName
        then Some children.[ent.FullName] else None
    member self.IsIgnoredEntity (ent: FSharpEntity) =
        ent.IsEnum
        || ent.IsInterface
        || ent.IsFSharpAbbreviation
        || isAttributeEntity ent
        || (hasIgnoredAtt ent.Attributes)
    /// Is compiler generated (CompareTo...) or belongs to ignored entity?
    /// (remember F# compiler puts class methods in enclosing modules)
    member self.IsIgnoredMethod (meth: FSharpMemberOrFunctionOrValue) =
        if (meth.IsCompilerGenerated && Naming.ignoredCompilerGenerated.Contains meth.CompiledName)
            || (hasIgnoredAtt meth.Attributes)
        then true
        else match tryFindChild meth.EnclosingEntity with
             | Some IgnoredEnt -> true
             | _ -> false
    member self.AddMethod (meth: FSharpMemberOrFunctionOrValue, methDecl: Fable.Declaration) =
        match tryFindChild meth.EnclosingEntity with
        | None ->
            if meth.IsModuleValueOrMember
                && not meth.Accessibility.IsPrivate
                && not meth.IsCompilerGenerated
                && not meth.IsExtensionMember then
                checkPublicNameConflicts meth.CompiledName
            decls.Add(Decl methDecl)
        | Some (Ent (_,_,entDecls,_)) -> entDecls.Add methDecl
        | Some _ -> () // TODO: log warning
    member self.AddDeclaration (decl: Fable.Declaration, ?publicName: string) =
        publicName |> Option.iter checkPublicNameConflicts
        decls.Add(Decl decl)
    member self.AddChild (com: IFableCompiler, ctx, newChild: FSharpEntity, privateName, newChildDecls: _ list) =
        if not newChild.Accessibility.IsPrivate then
            sanitizeEntityName newChild |> checkPublicNameConflicts
        let ent = Ent (com.GetEntity ctx newChild, privateName,
                    ResizeArray<_> newChildDecls,
                    getEntityLocation newChild |> makeRange)
        children.Add(newChild.FullName, ent)
        decls.Add(ent)
    member self.AddIgnoredChild (ent: FSharpEntity) =
        // Entities with no FullName will be abbreviations, so we don't need to
        // check if there're members in the enclosing module belonging to them
        match ent.TryFullName with
        | Some fullName -> children.Add(fullName, IgnoredEnt)
        | None -> ()
    member self.TryGetOwner (meth: FSharpMemberOrFunctionOrValue) =
        match tryFindChild meth.EnclosingEntity with
        | Some (Ent (ent,_,_,_)) -> Some ent
        | _ -> None
    member self.GetDeclarations (com, ctx): Fable.Declaration list =
        decls |> Seq.map (function
            | IgnoredEnt -> failwith "Unexpected ignored entity"
            | Decl decl -> decl
            | Ent (ent, privateName, decls, range) ->
                let range =
                    match decls.Count with
                    | 0 -> range
                    | _ -> range + (Seq.last decls).Range
                Fable.EntityDeclaration(ent, privateName, processMemberDecls com ctx ent decls, range))
        |> Seq.toList

let private tryGetRelativeImport (atts: #seq<FSharpAttribute>) =
    let emitAtt = atts |> tryFindAtt ((=) Atts.emit)
    let importAtt = atts |> tryFindAtt ((=) Atts.import)
    match emitAtt, importAtt with
    | None, Some att ->
        try
            match att.ConstructorArguments.[0], att.ConstructorArguments.[1] with
            | (_, (:? string as selector)), (_, (:? string as path))
                when path.StartsWith(".") -> Some(selector, path)
            | _ -> None
        with
        | _ -> None
    | _ -> None

let private transformMemberDecl (com: IFableCompiler) ctx (declInfo: DeclInfo)
    (meth: FSharpMemberOrFunctionOrValue) (args: FSharpMemberOrFunctionOrValue list list) (body: FSharpExpr) =
    let addMethod relativeImport =
        let memberName = sanitizeMethodName meth
        let memberLoc = getMemberLoc meth
        let ctx, privateName =
            // Bind module member names to context to prevent
            // name clashes (they will become variables in JS)
            if meth.EnclosingEntity.IsFSharpModule then
                let typ = makeType com ctx meth.FullType
                let ctx, privateName = bindIdent com ctx typ (Some meth) memberName
                ctx, Some (privateName.Name)
            else ctx, None
        let memberKind, args, body =
            match relativeImport with
            | Some(selector, path) ->
                Fable.Field, [], makeImport selector path
            | None ->
                let info =
                    { isInstance = meth.IsInstanceMember
                    ; passGenerics = hasAtt Atts.passGenerics meth.Attributes }
                bindMemberArgs com ctx info args
                |> fun (ctx, _, args) ->
                    if memberLoc <> Fable.StaticLoc
                    then { ctx with thisAvailability = ThisAvailable }, args
                    else ctx, args
                |> fun (ctx, args) ->
                    match meth.IsImplicitConstructor, declInfo.TryGetOwner meth with
                    | true, Some(EntityKind(Fable.Class(Some(fullName, _), _))) ->
                        { ctx with baseClass = Some fullName }, args
                    | _ -> ctx, args
                |> fun (ctx, args) ->
                    getMemberKind meth, args, transformExpr com ctx body
        let entMember =
            let fableEnt = makeEntity com ctx meth.EnclosingEntity
            let argTypes = List.map Fable.Ident.getType args
            let fullTyp = makeOriginalCurriedType com meth.CurriedParameterGroups body.Type
            match fableEnt.TryGetMember(memberName, memberKind, memberLoc, argTypes) with
            | Some m -> m
            | None -> makeMethodFrom com memberName memberKind memberLoc argTypes body.Type fullTyp None meth
            |> fun m -> Fable.MemberDeclaration(m, privateName, args, body, SourceLocation.Empty)
        declInfo.AddMethod(meth, entMember)
        declInfo, ctx
    let relativeImport = tryGetRelativeImport meth.Attributes
    if Option.isSome relativeImport
    then
        addMethod relativeImport
    elif declInfo.IsIgnoredMethod meth
    then
        declInfo, ctx
    elif isInline meth
    then
        // Inlining custom type operators is problematic, see #230
        if not meth.EnclosingEntity.IsFSharpModule && meth.CompiledName.StartsWith "op_"
        then
            sprintf "Custom type operators cannot be inlined: %s" meth.FullName
            |> attachRange (getRefLocation meth |> makeRange |> Some)
            |> Warning |> com.AddLog
            addMethod None
        else
            let vars = Seq.collect id args |> countRefs body
            com.AddInlineExpr meth.FullName (vars, body)
            declInfo, ctx
    else addMethod None

let rec private transformEntityDecl (com: IFableCompiler) ctx (declInfo: DeclInfo)
                                    (ent: FSharpEntity) subDecls =
    let relativeImport = tryGetRelativeImport ent.Attributes
    if Option.isSome relativeImport
    then
        let selector, path = relativeImport.Value
        let r = getEntityLocation ent |> makeRange
        let entName, body = sanitizeEntityName ent, makeImport selector path
        // Bind entity name to context to prevent name clashes
        let ctx, ident = bindIdent com ctx Fable.Any None entName
        let m = Fable.Member(entName, Fable.Field, Fable.StaticLoc, [], body.Type,
                            isPublic = not ent.Accessibility.IsPrivate)
        let decl = Fable.MemberDeclaration(m, Some ident.Name, [], body, r)
        let publicName =
            if ent.Accessibility.IsPrivate then None else Some entName
        declInfo.AddIgnoredChild ent
        declInfo.AddDeclaration(decl, ?publicName=publicName)
        declInfo, ctx
    elif declInfo.IsIgnoredEntity ent
    then
        declInfo.AddIgnoredChild ent
        declInfo, ctx
    else
    let childDecls = transformDeclarations com ctx subDecls
    if List.isEmpty childDecls && ent.IsFSharpModule
    then
        declInfo, ctx
    else
        // Bind entity name to context to prevent name
        // clashes (it will become a variable in JS)
        let ctx, ident = sanitizeEntityName ent |> bindIdent com ctx Fable.Any None
        declInfo.AddChild(com, ctx, ent, ident.Name, childDecls)
        declInfo, ctx

and private transformDeclarations (com: IFableCompiler) ctx decls =
    let declInfo, _ =
        decls |> List.fold (fun (declInfo: DeclInfo, ctx) decl ->
            match decl with
            | FSharpImplementationFileDeclaration.Entity (e, sub) ->
                transformEntityDecl com ctx declInfo e sub
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (meth, args, body) ->
                transformMemberDecl com ctx declInfo meth args body
            | FSharpImplementationFileDeclaration.InitAction (Transform com ctx e as fe) ->
                declInfo.AddDeclaration(Fable.ActionDeclaration (e, makeRange fe.Range))
                declInfo, ctx
        ) (DeclInfo(), ctx)
    declInfo.GetDeclarations(com, ctx)

let private getRootModuleAndDecls decls =
    let nameConflicts (decls: FSharpImplementationFileDeclaration list) =
        false // TODO
    let (|ModuleAndTypes|_|) (decls: FSharpImplementationFileDeclaration list) =
        (([], [], []), decls) ||> List.fold (fun (mods, typDecls, other) decl ->
            match decl with
            | FSharpImplementationFileDeclaration.Entity(e,subDecls) ->
                if e.IsFSharpModule
                then (e,subDecls)::mods, typDecls, other
                elif not e.IsNamespace && List.isEmpty subDecls // Type
                then mods, decl::typDecls, other
                else mods, typDecls, decl::other
            | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(m,_,_)
                when not m.EnclosingEntity.IsFSharpModule ->
                mods, decl::typDecls, other // Type members
            | _ ->
                mods, typDecls, decl::other)
        |> function
        | [mod'], typDecls, [] -> Some(mod', List.rev typDecls)
        | _ -> None
    let rec getRootModuleAndDecls outerEnt decls =
        match decls with
        | [FSharpImplementationFileDeclaration.Entity (ent, decls)]
                when ent.IsFSharpModule || ent.IsNamespace ->
            getRootModuleAndDecls (Some ent) decls
        | ModuleAndTypes((rootMod, modDecls), decls)
                 when nameConflicts (decls@modDecls) |> not ->
            (Some rootMod), decls@modDecls
        | decls -> outerEnt, decls
    getRootModuleAndDecls None decls

let makeFileMap (fileImpls: FSharpImplementationFileContents list)
                (filePairs: Map<string, string>) =
    fileImpls
    |> Seq.map (fun fileImpl ->
        let fileInfo: Fable.FileInfo =
            match getRootModuleAndDecls fileImpl.Declarations with
            | Some rootModule, _ ->
                sanitizeEntityFullName rootModule
            | None, _ -> ""
            |> fun ns -> {targetFile=filePairs.[fileImpl.FileName]; rootModule=ns}
        fileImpl.FileName, fileInfo)
    |> Map

type FableCompiler(com: ICompiler, projectMaps: Dictionary<string,Map<string, Fable.FileInfo>>,
                   entitiesCache: Dictionary<string, Fable.Entity>,
                   inlineExprsCache: Dictionary<string, Dictionary<FSharpMemberOrFunctionOrValue,int> * FSharpExpr>) =
    let replacePlugins =
        com.Plugins |> List.choose (function
            | path, (:? IReplacePlugin as plugin) -> Some (path, plugin)
            | _ -> None)
    let usedVarNames = HashSet<string>()
    member fcom.UsedVarNames = set usedVarNames
    interface IFableCompiler with
        member fcom.Transform ctx fsExpr =
            transformExpr fcom ctx fsExpr
        member fcom.IsReplaceCandidate ent =
            match ent.Assembly.FileName with
            | Some asmPath -> projectMaps.ContainsKey asmPath |> not
            | None -> false
        member fcom.TryGetInternalFile tdef =
            if (fcom :> IFableCompiler).IsReplaceCandidate tdef
            then None
            else Some (getEntityLocation tdef).FileName
        member fcom.GetEntity ctx tdef =
            entitiesCache.GetOrAdd(
                defaultArg tdef.TryFullName tdef.CompiledName,
                fun _ -> makeEntity fcom ctx tdef)
        member fcom.TryGetInlineExpr meth =
            let success, expr = inlineExprsCache.TryGetValue meth.FullName
            if success then Some expr else None
        member fcom.AddInlineExpr fullName inlineExpr =
            inlineExprsCache.AddOrUpdate(fullName,
                (fun _ -> inlineExpr), (fun _ _ -> inlineExpr))
            |> ignore
        member fcom.AddUsedVarName varName =
            usedVarNames.Add varName |> ignore
        member fcom.ReplacePlugins =
            replacePlugins
    interface ICompiler with
        member __.Options = com.Options
        member __.ProjDir = com.ProjDir
        member __.Plugins = com.Plugins
        member __.AddLog msg = com.AddLog msg
        member __.GetLogs() = com.GetLogs()
        member __.GetUniqueVar() = com.GetUniqueVar()

type FSProjectInfo(projectOpts: FSharpProjectOptions, filePairs: Map<string, string>,
                    ?fileMask: string, ?extra: Map<string, obj>) =
    let extra = defaultArg extra Map.empty
    let dependencies: IDictionary<string, string list> =
        ("dependencies", extra)
        ||> Map.findOrRun (fun () -> upcast Dictionary())
    let arePathsEqual p1 p2 =
        (Path.normalizeFullPath p1) = (Path.normalizeFullPath p2)
    member __.ProjectOpts = projectOpts
    member __.FilePairs = filePairs
    member __.FileMask = fileMask
    member __.Extra = extra
    member __.IsMasked(fileName) =
        match fileMask with
        | Some mask ->
            if arePathsEqual fileName mask
            then true
            else
                let success, deps = dependencies.TryGetValue(fileName)
                success && List.exists (arePathsEqual mask) deps
        | None -> true

let private getProjectMaps (com: ICompiler) (parsedProj: FSharpCheckProjectResults) (projInfo: FSProjectInfo) =
    parsedProj.ProjectContext.GetReferencedAssemblies()
    |> List.choose (fun assembly ->
        assembly.FileName
        |> Option.bind (fun asmPath ->
            try
                let asmDir = Path.GetDirectoryName(asmPath)
                let makeAbsolute (path: string) =
                    Path.GetFullPath(Path.Combine(asmDir, path))
                let json = File.ReadAllText(Path.ChangeExtension(asmPath, Naming.fablemapExt))
                let fableMap = Newtonsoft.Json.JsonConvert.DeserializeObject<Fable.FableMap>(json)
                fableMap.files |> Seq.map (fun kv ->
                    kv.Key, { kv.Value with targetFile = makeAbsolute kv.Value.targetFile })
                |> Map |> fun m -> Some(asmPath, m)
            with _ -> None // TODO: Raise error or warning?
        ))
    |> fun refAssemblies ->
        let dic = Dictionary()
        for (asm, map) in refAssemblies do
            dic.Add(asm, map)
        dic.Add(Naming.current, Map.empty)
        dic

let transformFiles (com: ICompiler) (parsedProj: FSharpCheckProjectResults) (projInfo: FSProjectInfo) =
    let projectMaps =
        ("projectMaps", projInfo.Extra)
        ||> Map.findOrRun (fun () -> getProjectMaps com parsedProj projInfo)
    // Cache for entities and inline expressions
    let entitiesCache = Dictionary<string, Fable.Entity>()
    let inlineExprsCache: Dictionary<string, Dictionary<FSharpMemberOrFunctionOrValue,int> * FSharpExpr> =
        Map.findOrNew "inline" projInfo.Extra
    // Start transforming files
    let entryFile =
        parsedProj.AssemblyContents.ImplementationFiles
        |> List.last |> fun file -> file.FileName
    parsedProj.AssemblyContents.ImplementationFiles
    |> Seq.choose (fun file ->
        try
        if not(projInfo.IsMasked file.FileName)
        then None
        else
            let fcom = FableCompiler(com, projectMaps, entitiesCache, inlineExprsCache)
            let rootEnt, rootDecls =
                let ctx = { Context.Empty with fileName = file.FileName }
                let rootEnt, rootDecls = getRootModuleAndDecls file.Declarations
                match rootEnt with
                | Some e when hasAtt Atts.erase e.Attributes -> makeEntity fcom ctx e, []
                | Some e -> makeEntity fcom ctx e, transformDeclarations fcom ctx rootDecls
                | None -> Fable.Entity.CreateRootModule file.FileName,
                            transformDeclarations fcom ctx rootDecls
            match rootDecls with
            | [] -> None
            | rootDecls ->
                let curProj = projectMaps.[Naming.current]
                let fileInfo: Fable.FileInfo = {targetFile=projInfo.FilePairs.[file.FileName]; rootModule=rootEnt.FullName}
                projectMaps.[Naming.current] <- Map.add file.FileName fileInfo curProj
                Fable.File(file.FileName, projInfo.FilePairs.[file.FileName], rootEnt, rootDecls,
                    isEntry=(file.FileName = entryFile), usedVarNames=fcom.UsedVarNames) |> Some
        with
        | :? FableError as e -> FableError(e.Message, ?range=e.Range, file=file.FileName) |> raise
        | ex -> exn (sprintf "%s (%s)" ex.Message file.FileName, ex) |> raise
    )
    |> fun seq ->
        let extra =
            projInfo.Extra
            |> Map.add "projectMaps" (box projectMaps)
            |> Map.add "inline" (box inlineExprsCache)
        extra, seq
