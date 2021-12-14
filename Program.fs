﻿open System
open Akka.FSharp
open FSharp.Json
open Akka.Actor
open Suave
open Suave.Http
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.RequestErrors
open Suave.Logging
open Suave.Utils
open System.IO
open System.Net
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Newtonsoft.Json
open System.Threading
open Suave.RequestErrors
open Newtonsoft.Json
open Suave.Writers


let system = ActorSystem.Create("TwitterServer")
let mutable hashTagsMap = Map.empty
let mutable mentionsMap = Map.empty
let mutable registeredUsersMap = Map.empty
let mutable followersMap = Map.empty

let initialCapacity = 101
let numProcs = Environment.ProcessorCount

type ResponseType = {
    userID: string
    message: string
    service: string
    code: string
}

type RequestType = {
    userID: string
    value: string
}

type Showfeed = 
    | RegisterNewUser of (string)
    | Subscribers of (string*string)
    | AddActiveUsers of (string*WebSocket)
    | RemoveActiveUsers of (string)
    | UpdateFeeds of (string*string*string)

type TweetMessages = 
    | InitTweet of (IActorRef)
    | AddRegisteredUser of (string)
    | TweetRequest of (string*string*string)

type RestResource<'a> = {
        Entry : RequestType -> string
}

let agent = MailboxProcessor<string*WebSocket>.Start(fun inbox ->
    let rec messageLoop() = async {
        let! msg,webSkt = inbox.Receive()
        let byteRes =
            msg
            |> System.Text.Encoding.ASCII.GetBytes
            |> ByteSegment
        let! _ = webSkt.send Text byteRes true
        return! messageLoop()
    }
    messageLoop()
)

let FeedActor (mailbox:Actor<_>) = 
    let mutable followers = Map.empty
    let mutable activeUsers = Map.empty
    let mutable feedtable = Map.empty
    let rec loop () = actor {
            let! message = mailbox.Receive() 
            match message with
            | RegisterNewUser(userId) ->
                followers <- Map.add userId Set.empty followers
                feedtable <- Map.add userId List.empty feedtable
                // printfn "Followers Map: %A" followers
            | Subscribers(userId, followerId) ->
                if followers.ContainsKey followerId then
                    let mutable followSet = Set.empty
                    followSet <- followers.[followerId]
                    followSet <- Set.add userId followSet
                    followers <- Map.remove followerId followers 
                    followers <- Map.add followerId followSet followers
                    // printfn "User %s started following you! for %s" userId followerId
                    let mutable jsonData: ResponseType = 
                        {userID = followerId; service= "Follow"; code = "OK"; message = sprintf "User %s started following you!" userId}
                    let mutable consJson = Json.serialize jsonData
                    // printfn "consjaosn = %A" consJson
                    agent.Post (consJson,activeUsers.[followerId])
            | AddActiveUsers(userId,userWebSkt) ->
                if activeUsers.ContainsKey userId then  
                    activeUsers <- Map.remove userId activeUsers
                activeUsers <- Map.add userId userWebSkt activeUsers 
                let mutable feedsPub = ""
                let mutable sertype = ""
                if feedtable.ContainsKey userId then
                    let mutable feedsTop = ""
                    let mutable fSize = 10
                    let feedList:List<string> = feedtable.[userId]
                    if feedList.Length = 0 then
                        sertype <- "Follow"
                        feedsPub <- sprintf "No feeds yet!!"
                    else
                        if feedList.Length < 10 then
                                fSize <- feedList.Length
                        // printfn"fsize = %d" fSize
                        for i in [0..(fSize-1)] do
                            // printfn "feed %d = %s" i feedtable.[userId].[i]
                            feedsTop <- "-" + feedtable.[userId].[i] + feedsTop

                        feedsPub <- feedsTop
                        sertype <- "LiveFeed"
                    // printfn "feeds pub = %A" feedsPub
                    let jsonData: ResponseType = {userID = userId; message = feedsPub; code = "OK"; service=sertype}
                    let consJson = Json.serialize jsonData
                    agent.Post (consJson,userWebSkt) 
            | RemoveActiveUsers(userId) ->
                if activeUsers.ContainsKey userId then  
                    activeUsers <- Map.remove userId activeUsers
            | UpdateFeeds(userId,tweetMsg,sertype) ->
                if followers.ContainsKey userId then
                    let mutable stype = ""
                    if sertype = "Tweet" then
                        stype <- sprintf "%s tweeted:" userId
                    else 
                        stype <- sprintf "%s re-tweeted:" userId
                    for foll in followers.[userId] do 
                        if followers.ContainsKey foll then
                            if activeUsers.ContainsKey foll then
                                let twt = sprintf "%s^%s" stype tweetMsg
                                let jsonData: ResponseType = {userID = foll; service=sertype; code="OK"; message = twt}
                                let consJson = Json.serialize jsonData
                                agent.Post (consJson,activeUsers.[foll])
                            let mutable listy = []
                            if feedtable.ContainsKey foll then
                                    listy <- feedtable.[foll]
                            listy  <- (sprintf "%s^%s" stype tweetMsg) :: listy
                            feedtable <- Map.remove foll feedtable
                            feedtable <- Map.add foll listy feedtable
            return! loop()
    }
    loop()

let feedActor = spawn system (sprintf "FeedActor") FeedActor

let liveFeed (webSocket : WebSocket) (context: HttpContext) =
    let rec loop() =
        let mutable presentUser = ""
        socket { 
            let! msg = webSocket.read()
            match msg with
            | (Text, data, true) ->
                let reqMsg = UTF8.toString data
                let parsed = Json.deserialize<RequestType> reqMsg
                presentUser <- parsed.userID
                feedActor <! AddActiveUsers(parsed.userID, webSocket)
                return! loop()
            | (Close, _, _) ->
                printfn "Closed WEBSOCKET"
                feedActor <! RemoveActiveUsers(presentUser)
                let emptyResponse = [||] |> ByteSegment
                do! webSocket.send Close emptyResponse true
            | _ -> return! loop()
        }
    loop()

let registerUser input =
    let mutable response = ""
    if registeredUsersMap.ContainsKey input.userID then
        let res: ResponseType = {userID = input.userID; 
                                message = sprintf "User %s already registred" input.userID; 
                                service = "Register";
                                code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        registeredUsersMap <- Map.add input.userID input.value registeredUsersMap
        followersMap <- Map.add input.userID Set.empty followersMap
        feedActor <! RegisterNewUser(input.userID)
        let res: ResponseType = {userID = input.userID; 
                                message = sprintf "User %s registred successfully" input.userID; 
                                service = "Register"; 
                                code = "OK"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    response

let loginUser input =
    let mutable response = ""
    if registeredUsersMap.ContainsKey input.userID then
        if registeredUsersMap.[input.userID] = input.value then
            let res: ResponseType = {userID = input.userID; 
                                    message = sprintf "User %s logged in successfully" input.userID; 
                                    service = "Login"; 
                                    code = "OK"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
        else 
            let res: ResponseType = {userID = input.userID; 
                                    message = "Invalid userid / password"; 
                                    service = "Login"; 
                                    code = "FAIL"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        let res: ResponseType = {userID = input.userID; 
                                message = "Invalid userid / password"; 
                                service = "Login"; 
                                code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    response

let followUser input =
    let mutable response = ""
    if input.value <> input.userID then
        if followersMap.ContainsKey input.value then
            if not (followersMap.[input.value].Contains input.userID) then
                let mutable followersSet = followersMap.[input.value]
                followersSet <- Set.add input.userID followersSet
                followersMap <- Map.remove input.value followersMap
                followersMap <- Map.add input.value followersSet followersMap
                feedActor <! Subscribers(input.userID,input.value) 
                let res: ResponseType = {userID = input.userID; 
                                            service="Follow"; 
                                            message = sprintf "You started following %s!" input.value; 
                                            code = "OK"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
            else 
                let res: ResponseType = {userID = input.userID; 
                                        service="Follow"; 
                                        message = sprintf "You are already following %s!" input.value; 
                                        code = "FAIL"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString      
        else  
            let res: ResponseType = {userID = input.userID; 
                                    service="Follow"; 
                                    message = sprintf "Invalid request, No such user (%s)." input.value; 
                                    code = "FAIL"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        let res: ResponseType = {userID = input.userID; 
                                service="Follow"; 
                                message = sprintf "You cannot follow yourself."; 
                                code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString    
    // printfn "follow response: %s" response
    response
    
let tweetUser userInput =
    let mutable response = ""
    if registeredUsersMap.ContainsKey userInput.userID then
        let mutable hashTag = ""
        let mutable mentionedUser = ""
        let words = userInput.value.Split ' '
        // printfn "words = %A" words
        for word in words do
            if word.Length > 0 then
                if word.[0] = '#' then
                    hashTag <- word.[1..(word.Length-1)]
                else if word.[0] = '@' then
                    mentionedUser <- word.[1..(word.Length-1)]

        if mentionedUser <> "" then
            if registeredUsersMap.ContainsKey mentionedUser then
                if not (mentionsMap.ContainsKey mentionedUser) then
                    mentionsMap <- Map.add mentionedUser List.empty mentionsMap
                let mutable mList = mentionsMap.[mentionedUser]
                mList <- (sprintf "%s tweeted:^%s" userInput.userID userInput.value) :: mList
                mentionsMap <- Map.remove mentionedUser mentionsMap
                mentionsMap <- Map.add mentionedUser mList mentionsMap
                feedActor <! UpdateFeeds(userInput.userID,userInput.value,"Tweet")
                let res: ResponseType = {userID = userInput.userID; 
                                        service="Tweet"; 
                                        message = (sprintf "%s tweeted:^%s" userInput.userID userInput.value); 
                                        code = "OK"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
            else
                let res: ResponseType = {userID = userInput.userID; 
                                        service="Tweet"; 
                                        message = sprintf "Invalid request, mentioned user (%s) is not registered" mentionedUser; 
                                        code = "FAIL"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
        else
            feedActor <! UpdateFeeds(userInput.userID,userInput.value,"Tweet")
            let res: ResponseType = {userID = userInput.userID; 
                                    service="Tweet"; 
                                    message = (sprintf "%s tweeted:^%s" userInput.userID userInput.value); 
                                    code = "OK"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString

        if hashTag <> "" then
            if not (hashTagsMap.ContainsKey hashTag) then
                hashTagsMap <- Map.add hashTag List.empty hashTagsMap
            let mutable tList = hashTagsMap.[hashTag]
            tList <- (sprintf "%s tweeted:^%s" userInput.userID userInput.value) :: tList
            hashTagsMap <- Map.remove hashTag hashTagsMap
            hashTagsMap <- Map.add hashTag tList hashTagsMap
    else  
        let res: ResponseType = {userID = userInput.userID; 
                                service="Tweet"; 
                                message = sprintf "Invalid request by user %s, Not registered yet!" userInput.userID; 
                                code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    response

let retweetUser userInput =
    let mutable resp = ""
    if registeredUsersMap.ContainsKey userInput.userID then
        feedActor <! UpdateFeeds(userInput.userID,userInput.value,"ReTweet")
        let rectype: ResponseType = {userID = userInput.userID; service="ReTweet"; message = (sprintf "%s re-tweeted:^%s" userInput.userID userInput.value); code = "OK"}
        resp <- rectype |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else  
        let res: ResponseType = {userID = userInput.userID; service="ReTweet"; message = sprintf "Invalid request by user %s, Not registered yet!" userInput.userID; code = "FAIL"}
        resp <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    resp

let query (userInput:string) = 
    let mutable tagsstring = ""
    let mutable mentionsString = ""
    let mutable resp = ""
    let mutable size = 10
    if userInput.Length > 0 then
        if userInput.[0] = '@' then
            let searchKey = userInput.[1..(userInput.Length-1)]
            if mentionsMap.ContainsKey searchKey then
                let mapData:List<string> = mentionsMap.[searchKey]
                if (mapData.Length < 10) then
                    size <- mapData.Length
                for i in [0..(size-1)] do
                    mentionsString <- mentionsString + "-" + mapData.[i]
                let res: ResponseType = {userID = ""; service="Query"; message = mentionsString; code = "OK"}
                resp <- Json.serialize res
            else 
                let res: ResponseType = {userID = ""; service="Query"; message = "-No tweets found for the mentioned user"; code = "OK"}
                resp <- Json.serialize res
        else
            let searchKey = userInput
            if hashTagsMap.ContainsKey searchKey then
                let mapData:List<string> = hashTagsMap.[searchKey]
                if (mapData.Length < 10) then
                        size <- mapData.Length
                for i in [0..(size-1)] do
                        tagsstring <- tagsstring + "-" + mapData.[i]
                let res: ResponseType = {userID = ""; service="Query"; message = tagsstring; code = "OK"}
                resp <- Json.serialize res
            else 
                let res: ResponseType = {userID = ""; service="Query"; message = "-No tweets found for the hashtag"; code = "OK"}
                resp <- Json.serialize res
    else
        let res: ResponseType = {userID = ""; service="Query"; message = "Type something to search"; code = "FAIL"}
        resp <- Json.serialize res
    resp


let getString (rawForm: byte[]) =
        System.Text.Encoding.UTF8.GetString(rawForm)
    
let fromJson<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a

let registerUserHandler = 
        request (fun r -> 
        r.rawForm
        |> getString
        |> fromJson<RequestType>
        |> registerUser
        |> JsonConvert.SerializeObject
        |> CREATED )
        >=> setMimeType "application/json"

let logUser = 
        request (fun r ->
        r.rawForm
        |> getString
        |> fromJson<RequestType>
        |> loginUser
        |> JsonConvert.SerializeObject
        |> CREATED )
        >=> setMimeType "application/json"

let followAUser=
        request (fun r ->
        r.rawForm
        |> getString
        |> fromJson<RequestType>
        |> followUser
        |> JsonConvert.SerializeObject
        |> OK )
        >=> setMimeType "application/json"

let sendTweetByUser =
        request (fun r ->
        r.rawForm
        |> getString
        |> fromJson<RequestType>
        |> tweetUser
        |> JsonConvert.SerializeObject
        |> CREATED )
        >=> setMimeType "application/json"

let sendRetweetByUser =
        request (fun r ->
        r.rawForm
        |> getString
        |> fromJson<RequestType>
        |> retweetUser
        |> JsonConvert.SerializeObject
        |> CREATED )
        >=> setMimeType "application/json"

let app =
    choose
        [ 
            path "/livefeed" >=> handShake liveFeed
        
            GET >=> choose [path "/" >=> file "Login.html"; browseHome]
            
            POST >=> choose
                    [   path "/register" >=> registerUserHandler
                        path "/login" >=> logUser
                        path "/follow" >=> followAUser 
                        path "/tweet" >=> sendTweetByUser
                        path "/retweet" >=> sendRetweetByUser
                    ]
                        
        ]

[<EntryPoint>]
let main argv =
        startWebServer defaultConfig app
        0
