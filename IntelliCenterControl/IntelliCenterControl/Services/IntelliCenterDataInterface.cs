﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IntelliCenterControl.Annotations;
using IntelliCenterControl.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace IntelliCenterControl.Services
{
    public class IntelliCenterDataInterface : IDataInterface<IntelliCenterConnection>
    {
        private HubConnection connection;
        private ClientWebSocket socketConnection;
        private IntelliCenterConnection _intelliCenterConnection = new IntelliCenterConnection();
        private readonly SemaphoreSlim _sendRateLimit = new SemaphoreSlim(1);
        private TimeSpan _sendRate = new TimeSpan(0, 0, 0, 0, 50);

        public event EventHandler<string> DataReceived;
        public event EventHandler<IntelliCenterConnection> ConnectionChanged;

        public Dictionary<string, string> Subscriptions = new Dictionary<string, string>();
        public Dictionary<Guid, string> UnsubscribeMessages = new Dictionary<Guid, string>();

        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();


        public async Task<bool> CreateConnectionAsync()
        {
             if (connection != null && connection.State == HubConnectionState.Connected)
            {
                await connection.StopAsync(Cts.Token);
            }

            if (socketConnection != null && socketConnection.State == WebSocketState.Open)
            {
                await socketConnection.CloseAsync(WebSocketCloseStatus.NormalClosure, "", Cts.Token);
            }

            _intelliCenterConnection.State = IntelliCenterConnection.ConnectionState.Disconnected;
            
            if (Settings.ServerURL.StartsWith("http"))
            {
                connection = new HubConnectionBuilder()
                    .WithUrl(Settings.ServerURL)
                    .WithAutomaticReconnect(new[] {TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)})
                    .Build();


                connection.KeepAliveInterval = TimeSpan.FromSeconds(5);

                connection.Reconnecting += error =>
                {
                    Debug.Assert(connection.State == HubConnectionState.Reconnecting);
                    _intelliCenterConnection.State = (IntelliCenterConnection.ConnectionState) connection.State;
                    OnConnectionChanged();
                    // Notify users the connection was lost and the client is reconnecting.
                    // Start queuing or dropping messages.

                    return Task.CompletedTask;
                };

                connection.Reconnected += connectionId =>
                {
                    Debug.Assert(connection.State == HubConnectionState.Connected);
                    _intelliCenterConnection.State = (IntelliCenterConnection.ConnectionState) connection.State;
                    OnConnectionChanged();
                    // Notify users the connection was reestablished.
                    // Start dequeuing messages queued while reconnecting if any.

                    return Task.CompletedTask;
                };

                connection.Closed += error =>
                {
                    Debug.Assert(connection.State == HubConnectionState.Disconnected);
                    _intelliCenterConnection.State = (IntelliCenterConnection.ConnectionState) connection.State;
                    OnConnectionChanged();
                    // Notify users the connection has been closed or manually try to restart the connection.

                    return Task.CompletedTask;
                };

                await connection.StartAsync(Cts.Token);

                if (connection.State == HubConnectionState.Connected) DataSubscribe();

                _intelliCenterConnection.State = (IntelliCenterConnection.ConnectionState) connection.State;
            }
            else if (Settings.ServerURL.StartsWith("ws"))
            {

                socketConnection = new ClientWebSocket();
                
                await socketConnection.ConnectAsync(new Uri(Settings.ServerURL), Cts.Token);

                if (socketConnection.State == WebSocketState.Open) DataSubscribe();

                _intelliCenterConnection.State = socketConnection.State switch
                {
                    WebSocketState.Aborted => IntelliCenterConnection.ConnectionState.Disconnected,
                    WebSocketState.Closed => IntelliCenterConnection.ConnectionState.Disconnected,
                    WebSocketState.CloseReceived => IntelliCenterConnection.ConnectionState.Disconnected,
                    WebSocketState.CloseSent => IntelliCenterConnection.ConnectionState.Disconnected,
                    WebSocketState.None => IntelliCenterConnection.ConnectionState.Disconnected,
                    WebSocketState.Connecting => IntelliCenterConnection.ConnectionState.Connecting,
                    WebSocketState.Open => IntelliCenterConnection.ConnectionState.Connected,
                    _ => IntelliCenterConnection.ConnectionState.Disconnected
                };
            }

            OnConnectionChanged();
            
            return await Task.FromResult(_intelliCenterConnection.State != IntelliCenterConnection.ConnectionState.Disconnected);
        }

        public async Task<bool> SendItemParamsUpdateAsync(string id, string prop, string data)
        {
            var message = CreateParameters(id, prop, data);

            if (!string.IsNullOrEmpty(message))
            {
                return (await SendMessage(message));
            }

            return await Task.FromResult(false);
        }

        public async Task<bool> SendItemCommandUpdateAsync(string id, string command, string data)
        {
            var message = CreateCommand(id, command, data);

            if (!string.IsNullOrEmpty(message))
            {
                return (await SendMessage(message));
            }

            return await Task.FromResult(false);
        }

        public async Task<bool> GetScheduleDataAsync()
        {
            var g = Guid.NewGuid();
            var cmd =
                "{ \"command\": \"GETPARAMLIST\", \"condition\": \"OBJTYP=SCHED\", \"objectList\": [{ \"objnam\": \"ALL\", \"keys\": " + Schedule.ScheduleKeys + " }], \"messageID\": \"" +
                g + "\" }";

            return (await SendMessage(cmd));

            return await Task.FromResult(false);
        }

        public async Task<bool> GetItemUpdateAsync(string id, string type)
        {
            if (Enum.TryParse<Circuit<IntelliCenterConnection>.CircuitType>(type, out var result))
            {
                var g = Guid.NewGuid();
                var key = String.Empty;

                switch (result)
                {
                    case Circuit<IntelliCenterConnection>.CircuitType.PUMP:
                        key = Pump.PumpKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.BODY:
                        key = Body.BodyKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.SENSE:
                        key = Sense.SenseKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CIRCUIT:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.GENERIC:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CIRCGRP:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CHEM:
                        key = Chem.ChemKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.HEATER:
                        key = Heater.HeaterKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.INTELLI:
                    case Circuit<IntelliCenterConnection>.CircuitType.GLOW:
                    case Circuit<IntelliCenterConnection>.CircuitType.MAGIC2:
                    case Circuit<IntelliCenterConnection>.CircuitType.CLRCASC:
                    case Circuit<IntelliCenterConnection>.CircuitType.DIMMER:
                    case Circuit<IntelliCenterConnection>.CircuitType.GLOWT:
                    case Circuit<IntelliCenterConnection>.CircuitType.LIGHT:
                        key = Light.LightKeys;
                        break;
                    default: break;
                }

                if (!string.IsNullOrEmpty(key))
                {
                    Subscriptions[id] = key;
                    var cmd =
                        "{ \"command\": \"GetParamList\", \"objectList\": [{ \"objnam\": \"" + id +
                        "\", \"keys\": " + key + " }], \"messageID\": \"" +
                        g.ToString() + "\" }";
                    return (await SendMessage(cmd));
                }
            }
            return await Task.FromResult(false);
        }


        public async Task<bool> SubscribeItemUpdateAsync(string id, string type)
        {
            if (Enum.TryParse<Circuit<IntelliCenterConnection>.CircuitType>(type, out var result))
            {
                var g = Guid.NewGuid();
                var key = String.Empty;

                switch (result)
                {
                    case Circuit<IntelliCenterConnection>.CircuitType.PUMP:
                        key = Pump.PumpKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.BODY:
                        key = Body.BodyKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.SENSE:
                        key = Sense.SenseKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CIRCUIT:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.GENERIC:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CIRCGRP:
                        key = Circuit<IntelliCenterConnection>.CircuitKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.CHEM:
                        key = Chem.ChemKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.HEATER:
                        key = Heater.HeaterKeys;
                        break;
                    case Circuit<IntelliCenterConnection>.CircuitType.INTELLI:
                    case Circuit<IntelliCenterConnection>.CircuitType.GLOW:
                    case Circuit<IntelliCenterConnection>.CircuitType.MAGIC2:
                    case Circuit<IntelliCenterConnection>.CircuitType.CLRCASC:
                    case Circuit<IntelliCenterConnection>.CircuitType.DIMMER:
                    case Circuit<IntelliCenterConnection>.CircuitType.GLOWT:
                    case Circuit<IntelliCenterConnection>.CircuitType.LIGHT:
                        key = Light.LightKeys;
                        break;
                    default: break;
                }

                if (!string.IsNullOrEmpty(key))
                {
                    Subscriptions[id] = key;
                    var cmd =
                        "{ \"command\": \"RequestParamList\", \"objectList\": [{ \"objnam\": \"" + id +
                        "\", \"keys\": " + key + " }], \"messageID\": \"" +
                        g.ToString() + "\" }";
                    return (await SendMessage(cmd));
                }
            }
            return await Task.FromResult(false);
        }

        public async Task<bool> SubscribeItemsUpdateAsync(IDictionary<string, string> items)
        {
            string message = "";

            foreach (var kvp in items)
            {
                if (Enum.TryParse<Circuit<IntelliCenterConnection>.CircuitType>(kvp.Value, out var result))
                {

                    switch (result)
                    {
                        case Circuit<IntelliCenterConnection>.CircuitType.PUMP:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Pump.PumpKeys + " }";
                            Subscriptions[kvp.Key] = Pump.PumpKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.BODY:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Body.BodyKeys + " }";
                            Subscriptions[kvp.Key] = Body.BodyKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.SENSE:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Sense.SenseKeys + " }";
                            Subscriptions[kvp.Key] = Sense.SenseKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.CIRCUIT:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Circuit<IntelliCenterConnection>.CircuitKeys + " }";
                            Subscriptions[kvp.Key] = Circuit<IntelliCenterConnection>.CircuitKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.GENERIC:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Circuit<IntelliCenterConnection>.CircuitKeys + " }";
                            Subscriptions[kvp.Key] = Circuit<IntelliCenterConnection>.CircuitKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.CIRCGRP:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Circuit<IntelliCenterConnection>.CircuitKeys + " }";
                            Subscriptions[kvp.Key] = Circuit<IntelliCenterConnection>.CircuitKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.CHEM:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Chem.ChemKeys + " }";
                            Subscriptions[kvp.Key] = Chem.ChemKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.HEATER:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Heater.HeaterKeys + " }";
                            Subscriptions[kvp.Key] = Heater.HeaterKeys;
                            break;
                        case Circuit<IntelliCenterConnection>.CircuitType.INTELLI:
                        case Circuit<IntelliCenterConnection>.CircuitType.GLOW:
                        case Circuit<IntelliCenterConnection>.CircuitType.MAGIC2:
                        case Circuit<IntelliCenterConnection>.CircuitType.CLRCASC:
                        case Circuit<IntelliCenterConnection>.CircuitType.DIMMER:
                        case Circuit<IntelliCenterConnection>.CircuitType.GLOWT:
                        case Circuit<IntelliCenterConnection>.CircuitType.LIGHT:
                            if (!string.IsNullOrEmpty(message)) message += ",";
                            message += "{ \"objnam\": \"" + kvp.Key +
                                       "\", \"keys\": " + Light.LightKeys + " }";
                            Subscriptions[kvp.Key] = Light.LightKeys;
                            break;
                        default: break;
                    }
                }

            }

            if (!string.IsNullOrEmpty(message))
            {
                var g = Guid.NewGuid();
                var cmd =
                    "{ \"command\": \"RequestParamList\", \"objectList\": [" + message + "], \"messageID\": \"" +
                    g.ToString() + "\" }";

                return (await SendMessage(cmd));
            }
            return await Task.FromResult(false);
        }

        public async Task<bool> UnSubscribeItemUpdate(string id)
        {
            if (Subscriptions.TryGetValue(id, out var keys))
            {
                var g = Guid.NewGuid();
                var cmd =
                    "{ \"command\": \"ReleaseParamList\", \"objectList\": [{ \"objnam\": \"" + id +
                    "\", \"keys\": " + keys + " }], \"messageID\": \"" +
                    g + "\" }";

                return (await SendMessage(cmd));
            }
            return await Task.FromResult(false);
        }

        public async Task<bool> UnSubscribeAllItemsUpdate()
        {
            if (Subscriptions.Count > 0)
            {
                var g = Guid.NewGuid();
                var cmd =
                    "{ \"command\": \"ClearParam\", \"messageID\": \"" + g + "\" }";

                return (await SendMessage(cmd));
            }
            return await Task.FromResult(false);
        }

        protected virtual void OnConnectionChanged()
        {
            EventHandler<IntelliCenterConnection> handler = ConnectionChanged;
            handler?.Invoke(this, _intelliCenterConnection);
        }

        protected virtual void OnDataReceived(string message)
        {
            EventHandler<string> handler = DataReceived;
            handler?.Invoke(this, message);
        }

        public async Task<bool> GetItemsDefinitionAsync(bool forceRefresh = false)
        {
            if (forceRefresh)
            {
                var g = Guid.NewGuid();
                var cmd =
                    "{ \"command\": \"GetQuery\", \"queryName\": \"GetHardwareDefinition\", \"arguments\": \" \", \"messageID\": \"" +
                    g.ToString() + "\" }";

                return(await SendMessage(cmd));
            }
            return await Task.FromResult(false);
        }



        private string CreateParameters(string objName, string property, string value)
        {
            var g = Guid.NewGuid();
            var paramsObject = "\"" + property + "\":\"" + value + "\"";

            var newobj = "{ \"objnam\": \"" + objName + "\", \"params\": {" + paramsObject + "}}";

            var message = "{ \"command\": \"SETPARAMLIST\", \"objectList\":[" + newobj + "], \"messageID\" : \"" + g.ToString() + "\" }";

            return message;
        }

        private string CreateCommand(string objName, string methodName, string value)
        {
            var g = Guid.NewGuid();
            var argsObject = "\"" + objName + "\":\"" + value + "\"";

            var newobj = "\"method\": \"" + methodName + "\", \"arguments\": {" + argsObject + "}";

            var message = "{ \"command\": \"SETCOMMAND\", " + newobj + ", \"messageID\" : \"" + g.ToString() + "\" }";

            return message;
        }

        private async Task<bool> SendMessage(string message)
        {
            try
            {
                if (connection != null && connection.State == HubConnectionState.Connected)
                {
                    await connection.InvokeAsync("Request", message, Cts.Token);
                    return await Task.FromResult(true);
                }
                else if (socketConnection != null && socketConnection.State == WebSocketState.Open)
                {
                    // Wait for any previous send commands to finish and release the semaphore
                    // This throttles our commands
                    await _sendRateLimit.WaitAsync(Cts.Token);
                    var byteMessage = Encoding.UTF8.GetBytes(message);
                    await socketConnection.SendAsync(byteMessage, WebSocketMessageType.Text, true, Cts.Token);
                    // Block other commands until our timeout to prevent flooding
                    await Task.Delay(_sendRate, Cts.Token);
                    // Exit our semaphore
                    _sendRateLimit.Release();
                    return await Task.FromResult(true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
            finally
            {
                // Exit our semaphore
                _sendRateLimit.Release();
            }

            return await Task.FromResult(false);
        }

        private async void DataSubscribe()
        {
            if (connection != null && connection.State == HubConnectionState.Connected)
            {
                try
                {
                    var stream = await connection.StreamAsChannelAsync<string>("Feed", Cts.Token);

                    while (connection != null && connection.State == HubConnectionState.Connected &&
                           await stream.WaitToReadAsync(Cts.Token))
                    {
                        while (stream.TryRead(out var count))
                        {
                            try
                            {
                                if (count.StartsWith('{'))
                                {
                                    var data = JsonConvert.DeserializeObject(count);
                                    if (data != null)
                                    {
                                        var jData = (JObject) data;
                                        if (jData.TryGetValue("command", out var commandValue))
                                        {
                                            switch (commandValue.ToString())
                                            {
                                                case "ClearParam":
                                                    Subscriptions.Clear();
                                                    UnsubscribeMessages.Clear();
                                                    break;
                                                case "ReleaseParamList":
                                                    if (jData.TryGetValue("messageID", out var g))
                                                    {
                                                        var gid = (Guid) g;
                                                        if (UnsubscribeMessages.TryGetValue(gid, out var id))
                                                        {
                                                            Subscriptions.Remove(id);
                                                            UnsubscribeMessages.Remove(gid);
                                                        }
                                                    }

                                                    break;
                                                default:
                                                    OnDataReceived(count);
                                                    break;
                                            }
                                        }
                                    }

                                    //Console.WriteLine($"{count}");
                                }
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                            }
                        }
                    }
                }
                catch (Exception e)
                {

                }
            }
            else if (socketConnection != null && socketConnection.State == WebSocketState.Open)
            {
                try
                {
                    await Task.Factory.StartNew(async () =>
                    {
                        while (socketConnection.State == WebSocketState.Open)
                        {
                            await ReadMessage();
                        }
                    }, Cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                catch (Exception e)
                {

                }
            }
        }

        private async Task ReadMessage()
        {
            WebSocketReceiveResult result;
            var message = new ArraySegment<byte>(new byte[4096]);
            try
            {
                await using var ms = new MemoryStream();
                do
                {
                    result = await socketConnection.ReceiveAsync(message, Cts.Token);
                    if (result.MessageType != WebSocketMessageType.Text)
                        return;
                    ms.Write(message.Array ?? throw new InvalidOperationException(), message.Offset, result.Count);
                } while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var receivedMessage = reader.ReadToEnd();
                //Console.WriteLine(receivedMessage);
                if (receivedMessage.StartsWith('{'))
                {
                    var data = JsonConvert.DeserializeObject(receivedMessage);
                    if (data != null)
                    {
                        var jData = (JObject)data;
                        if (jData.TryGetValue("command", out var commandValue))
                        {
                            switch (commandValue.ToString())
                            {
                                case "ClearParam":
                                    Subscriptions.Clear();
                                    UnsubscribeMessages.Clear();
                                    break;
                                case "ReleaseParamList":
                                    if (jData.TryGetValue("messageID", out var g))
                                    {
                                        var gid = (Guid)g;
                                        if (UnsubscribeMessages.TryGetValue(gid, out var id))
                                        {
                                            Subscriptions.Remove(id);
                                            UnsubscribeMessages.Remove(gid);
                                        }
                                    }

                                    break;
                                default:
                                    OnDataReceived(receivedMessage);
                                    break;
                            }
                        }
                    }
                    
                }

            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

        }
    }
}