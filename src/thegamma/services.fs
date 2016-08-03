﻿module TheGamma.Services

open Fable.Import
open Fable.Extensions
open TheGamma.Html
open TheGamma.Editors

// ------------------------------------------------------------------------------------------------
// Editors
// ------------------------------------------------------------------------------------------------

type EditorWorkerMessage = 
  | Update of string
  | UpdateNow of string
  | Refersh of string

type EditorService(checker, delay) = 
  let renderEditors = Control.Event<_>()
  let update text = async {
    let! prg = checker text 
        
    Log.trace("service", "Collecting editors")
    let! eds = Async.collect collectCmdEditors prg.Body 
    let eds = eds |> List.mapi (fun i v -> i, v)
    let filteredEds = 
      eds 
      |> List.filter (fun (i, ed1) ->
          eds |> List.exists (fun (j, ed2) -> j <> i && Ranges.strictSubRange ed1.Range ed2.Range) |> not)
      |> List.map snd

    Log.trace("service", "Rendering %s out of %s", filteredEds.Length, eds.Length)
    renderEditors.Trigger filteredEds }

  let agent = MailboxProcessor.Start(fun inbox ->
    let rec loop lastText pending = async {
      let! msg = inbox.Receive()
      match msg with
      | Update text -> 
          async { do! Async.Sleep(delay)
                  inbox.Post(Refersh text) } |> Async.StartImmediate
          return! loop lastText (pending+1)
      | UpdateNow text ->
          try
            Log.trace("editors", "updating...")
            if text <> lastText then do! update text
          with e -> 
            Log.exn("editors", "update failed: %O", e)
          return! loop text pending
      | Refersh text ->
          if pending = 1 then 
            try
              Log.trace("editors", "updating...")
              if text <> lastText then do! update text
            with e -> 
              Log.exn("editors", "update failed: %O", e)
          return! loop text (pending-1) }
    loop "" 0)

  member x.UpdateSource(text, ?immediately) = 
    if immediately = Some true then agent.Post(UpdateNow text)
    else agent.Post(Update text)
  member x.EditorsUpdated = renderEditors.Publish


// ------------------------------------------------------------------------------------------------
// Type checker
// ------------------------------------------------------------------------------------------------

type CheckingMessage = 
  | TypeCheck of code:string * AsyncReplyChannel<Program<Type>>
  | IsWellTyped of code:string * AsyncReplyChannel<bool>

type CheckingService(globals) =
  let errorsReported = Control.Event<_>()
  let emptyProg = { Body = []; Range = { Start = 0; End = 0 } }
  let agent = MailboxProcessor.Start(fun inbox ->
    let rec loop lastCode lastResult = async {
      let! msg = inbox.Receive()
      match msg with
      | IsWellTyped(code, repl) ->
          let! globals = Async.AwaitFuture globals
          try
            let! errors, result = TypeChecker.typeCheck globals code
            repl.Reply(errors.IsEmpty)
          with e ->
            Log.exn("service", "Type checking failed: %O", e)
            repl.Reply(false)
          return! loop lastCode lastResult

      | TypeCheck(code, repl) when code = lastCode ->
          Log.trace("service", "Returning previous result")
          repl.Reply(lastResult)
          return! loop lastCode lastResult

      | TypeCheck(code, repl) ->
          Log.trace("service", "Type checking source code")
          let! globals = Async.AwaitFuture globals
          try
            let! errors, result = TypeChecker.typeCheck globals code
            Log.trace("service", "Type checking completed")
            errorsReported.Trigger(errors)
            repl.Reply(result)
            return! loop code result
          with e ->
            Log.exn("service", "Type checking failed: %O", e)
            repl.Reply(emptyProg)
            return! loop lastCode lastResult }
    loop "" emptyProg)

  member x.ErrorsReported = errorsReported.Publish
  member x.TypeCheck(code) = agent.PostAndAsyncReply(fun ch -> TypeCheck(code, ch))
  member x.IsWellTyped(code) = agent.PostAndAsyncReply(fun ch -> IsWellTyped(code, ch))

