module UserSession

open System

open Akkling
open Akkling.Streams
open Akka.Streams
open Akka.Streams.Dsl

open Suave.Logging

open ChatUser
open ChannelFlow
open UserStore
open ChatServer

open FsChat
open ProtocolConv

type ChannelList = ChannelList of Map<ChannelId, UniqueKillSwitch>

let private logger = Log.create "usersession"

module private Implementation =
    let byChanId cid c = (c:ChannelData).id = cid

    let join serverChannelResult listenChannel (ChannelList channels) meUserId =
        match serverChannelResult, listenChannel with
        | Ok chan, Some listen ->
            let ks = listen chan.id (createChannelFlow chan.channelActor meUserId)
            Ok (channels |> Map.add chan.id ks |> ChannelList, chan)
        | Result.Error err, _ ->
            Result.Error ("getChannel failed: " + err)
        | _, None ->
            Result.Error "listenChannel is not set"

    let leave (ChannelList channels) chanId =
        match Map.tryFind chanId channels with
        | Some killswitch ->
            do killswitch.Shutdown()
            Ok (channels |> (Map.remove chanId >> ChannelList), ())
        | None ->
            Result.Error "User is not joined channel"

    let leaveAll (ChannelList channels) =
        do channels |> Map.iter (fun _ killswitch -> killswitch.Shutdown())
        Ok (ChannelList Map.empty, ())

    let replyErrorProtocol requestId errtext =
        Protocol.CannotProcess (requestId, errtext) |> Protocol.ClientMsg.Error

    let reply requestId = function
        | Ok response ->    response
        | Result.Error e -> replyErrorProtocol requestId e

    let (|CommandPrefix|_|) (p:string) (s:string) =
        if s.StartsWith(p, StringComparison.OrdinalIgnoreCase) then
            Some(s.Substring(p.Length).TrimStart())
        else
            None

    let isMember (ChannelList channels) channelId = channels |> Map.containsKey channelId

open Implementation
open Akka.Actor
type Session(server, userStore: UserStore, meArg) =

    let (RegisteredUser (meUserId, meUserInit)) = meArg
    let mutable meUser = meUserInit

    // session data
    let mutable channels = ChannelList Map.empty
    let mutable listenChannel = None

    let getMe () = RegisteredUser (meUserId, meUser)

    let updateChannels requestId f = function
        | Ok (newChannels, response) -> channels <- newChannels; f response
        | Result.Error e ->            replyErrorProtocol requestId e

    let makeChannelInfoResult v = async {
        match v with
        | Ok (arg1, channel: ChannelData) ->
            try
                let! (userIds: UserId list) = channel.channelActor <? ListUsers
                let! users = userStore.GetUsers userIds
                let chaninfo = { mapChanInfo channel with users = users |> List.map mapUserToProtocol}
                return Ok (arg1, chaninfo)
            with :?AggregateException as e ->
                do logger.error (Message.eventX "Error while communicating channel actor: {e}" >> Message.setFieldValue "e" e)
                return Result.Error "Channel is not available"
        | Result.Error e -> return Result.Error e
    }

    let notifyChannels message = async {
        do logger.debug (Message.eventX "notifyChannels")
        let (ChannelList channelList) = channels
        let! serverChannels = server |> (listChannels (fun {id = chid} -> Map.containsKey chid channelList))
        match serverChannels with
        | Ok list ->
            do logger.debug (Message.eventX "notifyChannels: {list}" >> Message.setFieldValue "list" list)
            list |> List.iter(fun chan -> chan.channelActor <! message)
        | _ ->
            do logger.error (Message.eventX "notifyChannel: Failed to get channel list")
            ()
        return ()
    }

    let updateStatus status = function
        | Anonymous person -> Anonymous {person with status = status}
        | Person person -> Person {person with status = status}
        | Bot bot -> Bot {bot with status = status}
        | other -> other

    let updateNick nick = function
        | Anonymous person -> Anonymous {person with nick = nick}
        | Person person -> Person {person with nick = nick}
        | Bot bot -> Bot {bot with nick = nick}
        | other -> other

    let updateAvatar ava =
        let imageUrl = if System.String.IsNullOrWhiteSpace ava then None else Some ava
        in
        function
        | Anonymous person -> Anonymous {person with imageUrl = imageUrl}
        | Person person -> Person {person with imageUrl = imageUrl}
        | Bot bot -> Bot {bot with imageUrl = imageUrl}
        | other -> other

    let updateUser requestId fn = function
        | s when System.String.IsNullOrWhiteSpace s ->
            async.Return <| replyErrorProtocol requestId "Invalid (blank) value is not allowed"
        | newNick -> async {
            let meNew = meUser |> fn newNick
            let! updateResult = userStore.Update (RegisteredUser (meUserId, meNew))
            match updateResult with
            | Ok (RegisteredUser(_, updatedUser)) ->
                meUser <- updatedUser
                do! notifyChannels (ParticipantUpdate meUserId)
                return Protocol.ClientMsg.UserUpdated (mapUserToProtocol <| getMe())
            | Result.Error e ->
                return replyErrorProtocol requestId e
        }

    let rec processControlMessage message =
        async {
            let requestId = "" // TODO take from server message
            let replyJoinedChannel chaninfo =
                chaninfo |> updateChannels requestId (fun ch -> Protocol.ClientMsg.JoinedChannel {ch with joined = true})
            match message with

            | Protocol.ServerMsg.Greets ->
                let makeChanInfo chanData =
                    { mapChanInfo chanData with joined = isMember channels chanData.id}

                let makeHello channels =
                    Protocol.ClientMsg.Hello {me = mapUserToProtocol <| getMe(); channels = channels |> List.map makeChanInfo}

                let! serverChannels = server |> (listChannels (fun _ -> true))
                return serverChannels |> (Result.map makeHello >> reply "")

            | Protocol.ServerMsg.Join (IsChannelId channelId) when isMember channels channelId ->
                return replyErrorProtocol requestId "User already joined channel"

            | Protocol.ServerMsg.Join (IsChannelId channelId) ->
                let! serverChannel = getChannel (byChanId channelId) server
                let result = join serverChannel listenChannel channels meUserId
                let! chaninfo = makeChannelInfoResult result
                return replyJoinedChannel chaninfo

            | Protocol.ServerMsg.Join _ ->
                return replyErrorProtocol requestId "bad channel id"

            | Protocol.ServerMsg.JoinOrCreate channelName ->
                // user channels are all created with autoRemove, system channels are not
                let! channelResult = server |> getOrCreateChannel channelName "" { ChannelConfig.Default with autoRemove = true }
                match channelResult with
                | Ok channelData when isMember channels channelData.id ->
                    return replyErrorProtocol requestId "User already joined channel"

                | Ok channelData ->
                    let! serverChannel = getChannel (byChanId channelData.id) server
                    let result = join serverChannel listenChannel channels meUserId
                    let! chaninfo = makeChannelInfoResult result
                    return replyJoinedChannel chaninfo
                | Result.Error err ->
                    return replyErrorProtocol requestId err

            | Protocol.ServerMsg.Leave chanIdStr ->
                return chanIdStr |> function
                    | IsChannelId channelId ->
                        let result = leave channels channelId
                        result |> updateChannels requestId (fun _ -> Protocol.LeftChannel chanIdStr)
                    | _ ->
                        replyErrorProtocol requestId "bad channel id"

            | Protocol.ServerMsg.ControlCommand {text = text; chan = chanIdStr } ->
                match text with
                | CommandPrefix "/leave" _ ->
                    return! processControlMessage (Protocol.ServerMsg.Leave chanIdStr)
                | CommandPrefix "/join" chanName ->
                    return! processControlMessage (Protocol.ServerMsg.JoinOrCreate chanName)
                | CommandPrefix "/nick" newNick ->
                    return! updateUser requestId updateNick newNick
                | CommandPrefix "/status" newStatus ->
                    return! updateUser requestId updateStatus newStatus
                | CommandPrefix "/avatar" newAvatarUrl ->
                    return! updateUser requestId updateAvatar newAvatarUrl
                | _ ->
                    return replyErrorProtocol requestId "command was not processed"

            | _ ->
                return replyErrorProtocol requestId "event was not processed"
        }

    let controlMessageFlow = Flow.empty<_, Akka.NotUsed> |> Flow.asyncMap 1 processControlMessage

    let serverEventsSource: Source<Protocol.ClientMsg, Akka.NotUsed> =
        let notifyNew sub = startSession server meUserId sub; Akka.NotUsed.Instance
        let source = Source.actorRef OverflowStrategy.Fail 1 |> Source.mapMaterializedValue notifyNew

        source |> Source.map (function
            | AddChannel ch -> ch |> (mapChanInfo >> Protocol.ClientMsg.NewChannel)
            | DropChannel ch -> ch |> (mapChanInfo >> Protocol.ClientMsg.RemoveChannel)
        )

    let controlFlow =
        Flow.empty<Protocol.ServerMsg, Akka.NotUsed>
        |> Flow.via controlMessageFlow
        |> Flow.mergeMat serverEventsSource Keep.left

    with
        member __.ControlFlow = controlFlow
        member __.SetListenChannel(lsn) = listenChannel <- lsn
