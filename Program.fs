open System
open Akka.FSharp
open FSharp.Json
open Akka.Actor

open Suave
open Suave.Operators
open Suave.Filters
open Suave.Successful
open Suave.Files
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Newtonsoft.Json
open Suave.Writers

let system = ActorSystem.Create("TwitterEngine")
let mutable hashTagsMap = Map.empty
let mutable mentionsMap = Map.empty
let mutable registeredUsersMap = Map.empty
let mutable followersMap = Map.empty

type Response = {
    userID: string
    message: string
    action: string
    code: string
}

type Request = {
    userID: string
    value: string
}

type Showfeed = 
    | RegisterUser of (string)
    | FollowUser of (string*string)
    | LoginUser of (string*WebSocket)
    | LogoutUser of (string)
    | UpdateFeeds of (string*string*string)

let websocket = MailboxProcessor<string*WebSocket>.Start(fun inbox ->
    let rec messageLoop() = async {
        let! message,ws = inbox.Receive()
        let byteArr =
            message
            |> System.Text.Encoding.ASCII.GetBytes
            |> ByteSegment
        let! _ = ws.send Text byteArr true
        return! messageLoop()
    }
    messageLoop()
)

let ServerActor (mailbox:Actor<_>) = 
    let mutable followers = Map.empty
    let mutable activeUsers = Map.empty
    let mutable feedtable = Map.empty
    let rec loop () = actor {
        let! message = mailbox.Receive() 
        match message with
        | RegisterUser(userId)                  ->  followers <- Map.add userId Set.empty followers
                                                    feedtable <- Map.add userId List.empty feedtable

        | FollowUser(userId, followerId)        ->  if followers.ContainsKey followerId then
                                                        let mutable followSet = Set.empty
                                                        followSet <- followers.[followerId]
                                                        followSet <- Set.add userId followSet
                                                        followers <- Map.remove followerId followers 
                                                        followers <- Map.add followerId followSet followers
                                                        let mutable json: Response = {userID = followerId; 
                                                                                      action= "Follow"; 
                                                                                      code = "OK"; 
                                                                                      message = sprintf "User %s started following you!" userId}
                                                        let mutable serJson = Json.serialize json
                                                        websocket.Post (serJson,activeUsers.[followerId])

        | LoginUser(userId,webSocket)          ->   if activeUsers.ContainsKey userId then  
                                                        activeUsers <- Map.remove userId activeUsers
                                                    activeUsers <- Map.add userId webSocket activeUsers 
                                                    let mutable feedstr = ""
                                                    let mutable actiontype = ""
                                                    if feedtable.ContainsKey userId then
                                                        let mutable feeds = ""
                                                        let mutable length = 10
                                                        let feedList:List<string> = feedtable.[userId]
                                                        if feedList.Length = 0 then
                                                            actiontype <- "Follow"
                                                            feedstr <- "No feeds yet!!"
                                                        else
                                                            if feedList.Length < 10 then
                                                                length <- feedList.Length
                                                            for i in [0..(length-1)] do
                                                                feeds <- "-" + feedtable.[userId].[i] + feeds

                                                            feedstr <- feeds
                                                            actiontype <- "LiveFeed"
                                                        let json: Response = {userID = userId; 
                                                                              message = feedstr; 
                                                                              code = "OK"; 
                                                                              action=actiontype}
                                                        let serializedJson = Json.serialize json
                                                        websocket.Post (serializedJson,webSocket) 

        | LogoutUser(userId)                    ->  if activeUsers.ContainsKey userId then  
                                                        activeUsers <- Map.remove userId activeUsers
                                                        
        | UpdateFeeds(userId,tweetMsg,actiontype)-> if followers.ContainsKey userId then
                                                        let mutable prefix = ""
                                                        if actiontype = "Tweet" then
                                                            prefix <- sprintf "%s tweeted:" userId
                                                        else 
                                                            prefix <- sprintf "%s re-tweeted:" userId
                                                        for followr in followers.[userId] do 
                                                            if followers.ContainsKey followr then
                                                                if activeUsers.ContainsKey followr then
                                                                    let tweet = sprintf "%s^%s" prefix tweetMsg
                                                                    let json: Response = {userID = followr; 
                                                                                          action=actiontype; 
                                                                                          code="OK"; 
                                                                                          message = tweet}
                                                                    let serJson = Json.serialize json
                                                                    websocket.Post (serJson,activeUsers.[followr])
                                                                let mutable fList = []
                                                                if feedtable.ContainsKey followr then
                                                                        fList <- feedtable.[followr]
                                                                fList  <- (sprintf "%s^%s" prefix tweetMsg) :: fList
                                                                feedtable <- Map.remove followr feedtable
                                                                feedtable <- Map.add followr fList feedtable
        return! loop()
    }
    loop()

let serverActor = spawn system "ServerActor" ServerActor

let liveFeed (webSocket : WebSocket) (httpContext: HttpContext) =
    let rec loop() =
        let mutable userId = ""
        socket { 
            let! message = webSocket.read()
            match message with
            | (Text, data, true) -> let requestMessage = UTF8.toString data
                                    let requestObj = Json.deserialize<Request> requestMessage
                                    userId <- requestObj.userID
                                    serverActor <! LoginUser(requestObj.userID, webSocket)
                                    return! loop()
            
            | (Close, _, _) ->      printfn "Closed WEBSOCKET"
                                    serverActor <! LogoutUser(userId)
                                    let emptyResponse = [||] |> ByteSegment
                                    do! webSocket.send Close emptyResponse true
            | _ -> return! loop()
        }
    loop()

let registerUser input =
    let mutable response = ""
    if registeredUsersMap.ContainsKey input.userID then
        let res: Response = {userID = input.userID; 
                            message = sprintf "User %s already registred" input.userID; 
                            action = "Register";
                            code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        registeredUsersMap <- Map.add input.userID input.value registeredUsersMap
        followersMap <- Map.add input.userID Set.empty followersMap
        serverActor <! RegisterUser(input.userID)
        let res: Response = {userID = input.userID; 
                            message = sprintf "User %s registred successfully" input.userID; 
                            action = "Register"; 
                            code = "OK"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    response

let loginUser input =
    let mutable response = ""
    if registeredUsersMap.ContainsKey input.userID then
        if registeredUsersMap.[input.userID] = input.value then
            let res: Response = {userID = input.userID; 
                                message = sprintf "User %s logged in successfully" input.userID; 
                                action = "Login"; 
                                code = "OK"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
        else 
            let res: Response = {userID = input.userID; 
                                message = "Invalid userid / password"; 
                                action = "Login"; 
                                code = "FAIL"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        let res: Response = {userID = input.userID; 
                            message = "Invalid userid / password"; 
                            action = "Login"; 
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
                serverActor <! FollowUser(input.userID,input.value) 
                let res: Response = {userID = input.userID; 
                                    action="Follow"; 
                                    message = sprintf "You started following %s!" input.value; 
                                    code = "OK"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
            else 
                let res: Response = {userID = input.userID; 
                                    action="Follow"; 
                                    message = sprintf "You are already following %s!" input.value; 
                                    code = "FAIL"}
                response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString      
        else  
            let res: Response = {userID = input.userID; 
                                action="Follow"; 
                                message = sprintf "Invalid request, No such user (%s)." input.value; 
                                code = "FAIL"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        let res: Response = {userID = input.userID; 
                            action="Follow"; 
                            message = sprintf "You cannot follow yourself."; 
                            code = "FAIL"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString    
    // printfn "follow response: %s" response
    response
    
let tweetUser input =
    let mutable response = ""
    
    let mutable hashTag = ""
    let mutable mentionedUser = ""
    let words = input.value.Split ' '
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
            mList <- (sprintf "%s tweeted:^%s" input.userID input.value) :: mList
            mentionsMap <- Map.remove mentionedUser mentionsMap
            mentionsMap <- Map.add mentionedUser mList mentionsMap
            serverActor <! UpdateFeeds(input.userID,input.value,"Tweet")
            let res: Response = {userID = input.userID; 
                                action="Tweet"; 
                                message = (sprintf "%s tweeted:^%s" input.userID input.value); 
                                code = "OK"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
        else
            let res: Response = {userID = input.userID; 
                                action="Tweet"; 
                                message = sprintf "Invalid request, mentioned user (%s) is not registered" mentionedUser; 
                                code = "FAIL"}
            response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    else
        serverActor <! UpdateFeeds(input.userID,input.value,"Tweet")
        let res: Response = {userID = input.userID; 
                            action="Tweet"; 
                            message = (sprintf "%s tweeted:^%s" input.userID input.value); 
                            code = "OK"}
        response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString

    if hashTag <> "" then
        if not (hashTagsMap.ContainsKey hashTag) then
            hashTagsMap <- Map.add hashTag List.empty hashTagsMap
        let mutable tList = hashTagsMap.[hashTag]
        tList <- (sprintf "%s tweeted:^%s" input.userID input.value) :: tList
        hashTagsMap <- Map.remove hashTag hashTagsMap
        hashTagsMap <- Map.add hashTag tList hashTagsMap
    
    response

let retweetUser input =
    let mutable response = ""
    
    serverActor <! UpdateFeeds(input.userID,input.value,"ReTweet")
    let res: Response = {userID = input.userID; 
                        action="ReTweet"; 
                        message = (sprintf "%s re-tweeted:^%s" input.userID input.value); 
                        code = "OK"}
    response <- res |> Json.toJson |> System.Text.Encoding.UTF8.GetString
    
    response

let getMentionUser (input:string) = 
    let mutable mention = ""
    let mutable response = ""
    let mutable size = 10
    let mentionUser = input.[1..(input.Length-1)]
    
    if mentionsMap.ContainsKey mentionUser then
        let mapData:List<string> = mentionsMap.[mentionUser]
        if (mapData.Length < 10) then
            size <- mapData.Length
        for i in [0..(size-1)] do
            mention <- mention + "-" + mapData.[i]
        let res: Response = {userID = ""; 
                            action="Query"; 
                            message = mention; 
                            code = "OK"}
        response <- Json.serialize res
    else 
        let res: Response = {userID = ""; 
                            action="Query"; 
                            message = "-No tweets found for the mentioned user"; 
                            code = "OK"}
        response <- Json.serialize res
    response

let getHashtag (input:string) = 
    let mutable hashtag = ""
    let mutable response = ""
    let mutable size = 10
    let hashtagName = input

    if hashTagsMap.ContainsKey hashtagName then
        let mapData:List<string> = hashTagsMap.[hashtagName]
        if (mapData.Length < 10) then
                size <- mapData.Length
        for i in [0..(size-1)] do
                hashtag <- hashtag + "-" + mapData.[i]
        let res: Response = {userID = ""; 
                            action="Query"; 
                            message = hashtag; 
                            code = "OK"}
        response <- Json.serialize res
    else 
        let res: Response = {userID = ""; 
                            action="Query"; 
                            message = "-No tweets found for the hashtag"; 
                            code = "OK"}
        response <- Json.serialize res
    response

let query (input:string) = 
    let mutable response = ""
    if input.Length > 0 then
        if input.[0] = '@' then
            response <- getMentionUser input
        else
            response <- getHashtag input
    else
        let res: Response = {userID = ""; 
                            action="Query"; 
                            message = "Search word missing"; 
                            code = "FAIL"}
        response <- Json.serialize res
    response

let registerUserHandler =  request (fun r -> r.rawForm
                                            |> System.Text.Encoding.UTF8.GetString
                                            |> JsonConvert.DeserializeObject<Request>
                                            |> registerUser
                                            |> JsonConvert.SerializeObject
                                            |> CREATED )
                                            >=> setMimeType "application/json"

let LoginUserHandler = request (fun r -> r.rawForm
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> JsonConvert.DeserializeObject<Request>
                                        |> loginUser
                                        |> JsonConvert.SerializeObject
                                        |> CREATED )
                                        >=> setMimeType "application/json"

let followUserHandler = request (fun r -> r.rawForm
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> JsonConvert.DeserializeObject<Request>
                                        |> followUser
                                        |> JsonConvert.SerializeObject
                                        |> OK )
                                        >=> setMimeType "application/json"

let tweetHandler = request (fun r -> r.rawForm
                                    |> System.Text.Encoding.UTF8.GetString
                                    |> JsonConvert.DeserializeObject<Request>
                                    |> tweetUser
                                    |> JsonConvert.SerializeObject
                                    |> CREATED )
                                    >=> setMimeType "application/json"

let retweetHandler = request (fun r -> r.rawForm
                                        |> System.Text.Encoding.UTF8.GetString
                                        |> JsonConvert.DeserializeObject<Request>
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
                        path "/login" >=> LoginUserHandler
                        path "/follow" >=> followUserHandler 
                        path "/tweet" >=> tweetHandler
                        path "/retweet" >=> retweetHandler
                    ]

            pathScan "/search/%s"
                (fun searchWord ->
                  let response = query searchWord
                  OK response) 
                        

        ]

[<EntryPoint>]
let main argv =
    startWebServer defaultConfig app
    0
