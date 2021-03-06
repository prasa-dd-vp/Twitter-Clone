# Twitter-Clone
Twitter Clone consists of a server engine that uses the Suave web framework to implement a websocket interface and supports tweet, follow, live feed display, retweet and query functionalities using the actor model in Akka.NET framework. The client side implementation consists of a basic twitter-like UI developed using HTML, CSS and Javascript and uses websockets to connect and receive updates from the server.  

The parts of Twitter Simulation(Part 1) have been modified and rewritten using a web framework to be reused in this clone.  

## Steps to Execute the project
1. Unzip the file project4_II.zip and go to folder project4_II.
2. First start the server using the command - dotnet run Program.fs and the server by default runs on port 8080.
3. Then run the client in the browser at localhost:8080 and the login/register page will be loaded.
4. Multiple clients can be run at the same time by using the above url on multiple tabs or windows. 

Create Account - In the register page loaded after starting the client, give a suitable username, password and click Register Now, so that the user gets added to the registered list of users. Then use the same credentials to login in to the homepage.

## Implementation Details
### Client:
The client side sends HTTP requests to the server for actions like follow, search, tweet and login and establishes a websocket connection for each user during login which is used to deliver feeds to the users in real time. Every request sent is in JSON format. Once the response is received, it is displayed to the user (both errors and success msgs are handled).  

### Server:
The server engine shows live feed to users and notifies the users about follow, tweet, retweet, etc., in real time without the client having to request for any updates. The Websocket interface is used to send updates to online users as and when available. Actors in the Akka.NET framework are used for concurrently showing appropriate feeds to all online users. The response sent to clients is in JSON format. Both REST APIs and Websockets are used for different actions.  Websockets have been used in situations where live updates need to be sent to the users dynamically and are not based on the request from clients.

### HTTP Request and Response:
HTTP request and response are used for the following functionalities:
* Register
* Login
* Tweet
* Retweet
* Follow
* Search

### Websocket:
Websocket is used for sending the following:
* Tweet notification
* Follow notification
