﻿using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.ServiceBus;
using Microsoft.ServiceBus.Messaging;
using Newtonsoft.Json;
using Worker.Converters;

namespace Worker
{
    /// <summary>
    /// Handles retrieving messages from the request queue and sending the responses to the master.
    /// </summary>
    class MessageHandler
    {
        private readonly string _masterUrl = "http://ptmaster.azurewebsites.net/response";
        private readonly string _ptQueue = "ptclient";
        private QueueClient _ptQueueClient;

        public MessageHandler()
        {
            StartHandler();
        }

        private void StartHandler()
        {
            var manager = NamespaceManager.Create();
            if (!manager.QueueExists(_ptQueue))
            {
                //Queue does notexist, so it returns and the program shuts down.
                return;
            }
            _ptQueueClient = QueueClient.Create(_ptQueue);
            StartListening();
        }

        /// <summary>
        /// Starts listening to the request queue.
        /// </summary>
        public void StartListening()
        {
            //TODO:Maybe provide a way to turn it off somehow
            while (true)
            {
                try
                {
                    var message = _ptQueueClient.Receive();
                    if (message != null)
                    {
                        //Starts a task so that it can start listening for more messages.
                        Task.Run(() => ProcessMessage(message));
                    }
                    else
                    {
                        break;
                    }
                }
                catch (Exception)
                {
                    //Error handling
                }
            }
        }

        /// <summary>
        /// Attempts to Deserialize the incoming message to a Request object, then excutes it.
        /// </summary>
        /// <param name="message"></param>
        public void ProcessMessage(BrokeredMessage message)
        {
            WorkerRequest request;
            try
            {
                request = JsonConvert.DeserializeObject<WorkerRequest>(message.GetBody<string>(), new CommandConverter());
            }
            catch (Exception)
            {
                //The message could not be parsed. Calls "message.DeadLetter();" so the message isn't read again and again.
                //TODO:Needs a way to let master know that a request was in wrong format so that master can let the user know.
                message.DeadLetter();
                throw;
            }
            try
            {
                //Worker implements IDisposable, so the using statement makes sure everything is disposed of.
                WorkerResponse response;
                using (var worker = new PhantomWorker())
                {
                    response = worker.ExecuteRequest(request);
                }
                SendResponse(response);
                message.Complete();
            }
            catch (Exception)
            {
                //Something went wrong with the worker.
                //TODO:Let user know something went wrong and he should try again
                message.DeadLetter();
                throw;
            }
        }

        /// <summary>
        /// Sends the response message to the master using HTTP Post.
        /// </summary>
        /// <param name="body">The body of the message. Should be a serialized "Response" model.</param>
        public void SendResponse(WorkerResponse response)
        {
            using (var client = GetClient())
            {
                Task.Run(() =>
                {
                    var postAsJsonAsync = client.PostAsJsonAsync(_masterUrl, response).Result;
                    var statusCode = postAsJsonAsync.StatusCode;
                    if (statusCode != HttpStatusCode.Accepted)
                    {
                        //Something went wrong with the post
                    }
                });
            }
        }

        /// <summary>
        /// Returns an HttpClient thats ready to be used.
        /// </summary>
        /// <returns></returns>
        private HttpClient GetClient()
        {
            var client = new HttpClient {BaseAddress = new Uri(_masterUrl)};
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = new TimeSpan(0, 0, 30);
            return client;
        }
    }
}