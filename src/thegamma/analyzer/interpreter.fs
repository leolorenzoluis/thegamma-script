﻿module TheGamma.Interpreter

open TheGamma
open TheGamma.Ast
open TheGamma.Common
open TheGamma.Babel
open System.Collections.Generic

// ------------------------------------------------------------------------------------------------
// 
// ------------------------------------------------------------------------------------------------

[<Emit("eval($0)")>]
let eval (s:string) : RuntimeValue = failwith "JS only"

type BabelOptions = 
  { presets : string[] }

type BabelResult = 
  { code : string }
type Babel =
  abstract transformFromAst : obj * string * BabelOptions -> BabelResult

[<Emit("Babel")>]
let babel : Babel = Unchecked.defaultof<_> 




// Above copy paste from CodeGen

type EvaluationContext =
  { Globals : IDictionary<string, Entity> }

let (|FindProperty|_|) (name:Name) { Members = membs } = 
  membs |> Seq.tryPick (function 
    Member.Property(name=n; emitter=e) when n = name.Name -> Some(e) | _ -> None) 

let (|FindMethod|_|) (name:Name) { Members = membs } = 
  membs |> Seq.tryPick (function 
    Member.Method(name=n; arguments=args; emitter=e) when n = name.Name -> Some(args, e) | _ -> None) 

let storeArguments values =
  values |> Array.ofList, 
  values |> List.mapi (fun i _ ->
    MemberExpression
      ( IdentifierExpression("_stored", None),
        NumericLiteral(float i, None), true, None ))

let evaluateExpression (_stored:RuntimeValue[]) (expr:Expression) =
  let prog = { Babel.Program.location = None; Babel.Program.body = [ExpressionStatement(expr, None)] }
  let code = babel.transformFromAst(Serializer.serializeProgram prog, "", { presets = [| "es2015" |] })
  Log.trace("interpreter", "Interpreter evaluating: %O", code.code)
  try
    // Get fable to reference everything
    let s = TheGamma.Series.series<int, int>.create(async { return [||] }, "", "", "") 
    TheGamma.TypePovidersRuntime.RuntimeContext("lol", "", "troll") |> ignore
    TheGamma.TypePovidersRuntime.trimLeft |> ignore
    TheGamma.GoogleCharts.chart.bar |> ignore
    TheGamma.table<int, int>.create(s) |> ignore
    TheGamma.Maps.timeline<int, int>.create(s) |> ignore
    TheGamma.Series.series<int, int>.values([| 1 |]) |> ignore    
    
    // The name `_stored` may appear in the generated code!
    _stored.Length |> ignore

    eval(code.code)
  with e ->
    Log.exn("interpreter", "Evaluation failed: %O", e)
    reraise()

let evaluateCall emitter inst args =
  let _stored, args = storeArguments (inst::args)
  evaluateExpression _stored (emitter.Emit(List.head args, List.tail args))

let evaluatePreview typ value = 
  let previewName = {Name.Name="preview"}
  match typ with
  | Some(Type.Object(FindProperty previewName e)) -> Some(evaluateCall e value [])
  | _ -> None

let rec ensureValue ctx (e:Entity) = 
  if e.Value.IsNone then
    match evaluateEntity ctx e with
    | Some value ->
        e.Value <- Some { Value = value; Preview = evaluatePreview e.Type value }
    | _ -> ()

and getValue ctx (e:Entity) = 
  if e.Value.IsNone then Log.error("interpreter", "getValue: Value of entity %O has not been evaluated.", e)
  e.Value.Value.Value

and evaluateEntity ctx (e:Entity) = 
  match e.Kind with
  | EntityKind.Constant(Constant.Boolean b) -> Some(unbox b)
  | EntityKind.Constant(Constant.Number n) -> Some(unbox n)
  | EntityKind.Constant(Constant.String s) -> Some(unbox s)
  | EntityKind.Constant(Constant.Empty) -> Some(unbox null)

  | EntityKind.GlobalValue(name) ->
      match ctx.Globals.TryGetValue name.Name with
      | true, { Value = Some value } -> Some value.Value
      | _ -> None
      
  | EntityKind.ChainElement(isProperty=true; name=name; instance=Some inst) ->
      match Types.reduceType inst.Type.Value with 
      | Type.Object(FindProperty name e) -> 
          Some(evaluateCall e (getValue ctx inst) [])
      | _ -> None

  | EntityKind.ChainElement(isProperty=false; name=name; instance=Some inst; 
        arguments=Some { Kind = EntityKind.ArgumentList(args) }) ->
      let pb = args |> List.takeWhile (function { Kind = EntityKind.NamedParam _ } -> false | _ -> true)  
      let nb = args |> List.skipWhile (function { Kind = EntityKind.NamedParam _ } -> false | _ -> true)  
      let positionBased = pb |> List.map (getValue ctx) |> Array.ofList
      let nameBased = 
        nb |> List.choose(function 
          | { Kind = EntityKind.NamedParam(name, value) } -> Some(name.Name, getValue ctx value)
          | _ -> None) |> dict

      match Types.reduceType inst.Type.Value with 
      | Type.Object(FindMethod name (pars, e)) -> 
          let args = pars |> List.mapi (fun i (name, _, _) ->
            if i < positionBased.Length then positionBased.[i]
            elif nameBased.ContainsKey(name) then nameBased.[name]
            else (unbox null) )
          Some(evaluateCall e (getValue ctx inst) args)
      | _ -> None

  | _ -> None

let evaluateEntityTree ctx (e:Entity) = 
  let visited = Dictionary<Symbol, bool>()
  let rec loop (e:Entity) = 
    if not (visited.ContainsKey(e.Symbol)) && e.Value.IsNone then
      visited.[e.Symbol] <- true
      for e in e.Antecedents do loop e
      ensureValue ctx e
  loop e
  e.Value 

// ------------------------------------------------------------------------------------------------
// 
// ------------------------------------------------------------------------------------------------

let globalEntity name typ expr =
  let value = 
    match expr with
    | Some expr -> 
        let value = evaluateExpression [| |] expr
        Some { Value = value; Preview = evaluatePreview (Some typ) value }
    | _ -> None

  Log.trace("interpreter", "Initializing global value '%s' = %O", name, value)
  { Kind = EntityKind.GlobalValue({ Name = name })
    Symbol = createSymbol()
    Type = Some typ
    Meta = []
    Value = value
    Errors = [] }

let evaluate (globals:seq<Entity>) (e:Entity) = 
  Log.trace("interpreter", "Evaluating entity %s (%O)", e.Name, e.Kind)
  let ctx = { Globals = dict [ for e in globals -> e.Name, e ] }
  let res = evaluateEntityTree ctx e
  Log.trace("interpreter", "Evaluated entity %s (%O) = %O", e.Name, e.Kind, res)
  res