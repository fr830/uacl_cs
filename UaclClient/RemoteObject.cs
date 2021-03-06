﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UaclUtils;
using UnifiedAutomation.UaBase;
using UnifiedAutomation.UaClient;

namespace UaclClient
{
    /**
     * Thats the 'main' class for a UA Client Connection.
     *
     * You've to use it to encapsulate a closed application context. For every such context we establish a session. It's
     * BTW a good idea to have that in mind, if you design your server architecture.
     *
     * To get a working UA Client Connection you should instantiate *RemoteObject* with the connection information and
     * the UA Node Address Path from the server side. The separator is the '.'!
     *
     * In a good .NET software design we would suggest to have an instance variable of a RemoteObject. The instance
     * variable is the correct handle to do all the necessary capabilties on the server.
     */
    public class RemoteObject
    {
        public RemoteObject(string ip, int port, string name)
        {
            Connection = new ConnectionInfo(ip, port, name);
            Name = name;
            MyNodeId = NodeId.Null;
            NodeIdCache = new Dictionary<string, NodeId>();
            SessionLock = new object();
            ConnectionEstablishmentIsWorking = false;
            ConnectionEstablishmentLock = new object();
            StartConnectionEstablishmentCallback = () =>
            {
                if (ConnectionEstablishmentIsWorking) return;

                try
                {
                    lock (ConnectionEstablishmentLock)
                    {
                        ConnectionEstablishmentIsWorking = true;
                    }

                    while (true)
                    {
                        try
                        {
                            lock (SessionLock)
                            {
                                if (SessionHandle == null)
                                {
                                    SessionHandle = new OpcUaSessionHandle(OpcUaSession.Create(Connection));
                                }
                            }
                        }
                        catch (Exception exc)
                        {
                            Logger.Warn(
                                $"Exception at connection establishment while 'new OpcUaSessionHandle()' ... '{exc.Message}'.");
                            Thread
                                .Sleep(1000); // It's maybe a good idea, not to have a hectic connection establishment.
                            continue;
                        }

                        try
                        {
                            if (Connect())
                            {
                                return;
                            }
                        }
                        catch (Exception exc)
                        {
                            Logger.Warn(
                                $"Exception at connection establishment while 'Connect()' ... '{exc.Message}'.");
                            Thread
                                .Sleep(1000); // It's maybe a good idea, not to have a hectic connection establishment.
                        }
                    }
                }
                finally
                {
                    lock (ConnectionEstablishmentLock)
                    {
                        ConnectionEstablishmentIsWorking = false;
                    }
                }
            };

            StartConnectionEstablishment(); // Yup, we'll call it while RemoteObject creation, directly.
        }

        private object ConnectionEstablishmentLock { get; set; }
        private bool ConnectionEstablishmentIsWorking { get; set; }
        private ThreadStart StartConnectionEstablishmentCallback { get; set; }

        public void StartConnectionEstablishment()
        {
            if (ConnectionEstablishmentIsWorking) return;
            var thread = new Thread(StartConnectionEstablishmentCallback);
            thread.Start();
        }

        private Action<Session, ServerConnectionStatusUpdateEventArgs> NotConnectedCallback { get; set; }

        private Action<Session, ServerConnectionStatusUpdateEventArgs> PostConnectionEstablished { get; set; }

        public NodeId MyNodeId { get; set; }
        public Dictionary<string, NodeId> NodeIdCache { get; set; }

        public void SetDisconnectedHandler(Action<Session, ServerConnectionStatusUpdateEventArgs> handler)
        {
            AnnounceSessionNotConnectedHandler(handler);
        }

        protected void AnnounceSessionNotConnectedHandler(Action<Session, ServerConnectionStatusUpdateEventArgs> notConnected)
        {
            NotConnectedCallback = notConnected;
        }

        public void SetConnectedHandler(Action<Session, ServerConnectionStatusUpdateEventArgs> handler)
        {
            AnnouncePostConnectionEstablishedHandler(handler);
        }
        
        protected void AnnouncePostConnectionEstablishedHandler(Action<Session, ServerConnectionStatusUpdateEventArgs> postConnectionEstablished)
        {
            PostConnectionEstablished = postConnectionEstablished;
        }

        private bool AnnounceToSession()
        {
            ServerConnectionStatusUpdateEventHandler statusChangedCallback = (s, args) =>
            {
                switch (s.ConnectionStatus)
                {
                    case ServerConnectionStatus.ConnectionErrorClientReconnect:
                    case ServerConnectionStatus.Disconnected:
                    case ServerConnectionStatus.LicenseExpired:
                    case ServerConnectionStatus.ServerShutdown:
                    case ServerConnectionStatus.ServerShutdownInProgress:
                        NotConnectedCallback?.Invoke(s, args);
                        // My idea was, to call Dispose() here, but I think, we should do it from
                        // outside the RemoteObject context ...
                        return;
                    case ServerConnectionStatus.Connected:
                    case ServerConnectionStatus.SessionAutomaticallyRecreated:
                        PostConnectionEstablished?.Invoke(s, args);
                        // I think, it's a good idea, to have a callback like this. So you can provide e. g. the
                        // monitoring of some UA variables here.
                        return;
                    default:
                        /*
                            case ServerConnectionStatus.Connecting:
                            case ServerConnectionStatus.ConnectionWarningWatchdogTimeout:
                        */
                        return;
                }
            };

            return SessionHandle.AddStatusChangedHandler(statusChangedCallback);
        }

        public bool Connected()
        {
            lock (SessionLock)
            {
                return ConnectedNonSync();
            }
        }

        private bool ConnectedNonSync()
        {
            return SessionHandle != null &&
                SessionHandle.Session.ConnectionStatus == ServerConnectionStatus.Connected;
        }

        public bool Connect()
        {
            while(SessionHandle == null)
            {
                Thread.Sleep(50);
            }

            lock (SessionLock)
            {
                if (ConnectedNonSync()) return true;

                if (SessionHandle == null || SessionHandle.Timeout) return false;

                var session = SessionHandle.Session;
                var stopWatch = Stopwatch.StartNew();

                if (AnnounceToSession())
                {
                    Logger.Info("ConnectionStatusChanged callback successfull registered!");
                }

                do
                {
                    try
                    {
                        Logger.Info($"Try to connect to:{session.SessionUri.Uri.AbsoluteUri}");
                        session.Connect(session.SessionUri.Uri.AbsoluteUri, SecuritySelection.None);
                        Logger.Info($"Connection to {session.SessionUri.Uri.AbsoluteUri} established.");
                    }
                    catch (Exception e)
                    {
                        ExceptionHandler.Log(e,
                            $"An error occurred while try to connect to server: {session.SessionUri.Uri.AbsoluteUri}.");
                    }

                    stopWatch.Stop();

                    if (stopWatch.Elapsed.Seconds > 5) break;

                    stopWatch.Start();

                } while (session.NotConnected());

                if (session.NotConnected())
                {
                    SessionHandle.Timeout = true;
                    return false;
                }
            }

            return Connected();
        }

        public void Disconnect()
        {
            if (!Connected()) return;

            lock (SessionLock)
            {
                SessionHandle?.Dispose();
            }
        }

        public OpcUaSessionHandle SessionHandle { get; private set; }

        private ConnectionInfo Connection { get; }

        public string Name { get; }

        public void Monitor(Dictionary<string, Action<Variant>> monitors)
        {
            Execute(() =>
            {
                var rh = new RemoteHelper(this);
                rh.MonitorDataChanges(monitors.Keys.Select(name => new RemoteDataMonitor
                    {
                        Name = name,
                        Value = Variant.Null,
                        Callback = monitors[name]
                    })
                    .ToList(), this);
                return Variant.Null;
            });
        }

        public void Monitor(string name, Action<Variant> action)
        {
            try
            {
                var monitor = new RemoteDataMonitor
                {
                    Name = name,
                    Value = Variant.Null,
                    Callback = action
                };

                monitor.Announce(this);
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, $"Cannot subscribe MONITORED ITEM '{Name}.{name}'.");
            }
        }

        protected void Invoke(string name, params object[] parameters)
        {
            var method = new RemoteMethod
            {
                Name = name,
                InputArguments = parameters.Select(iA => TypeMapping.ToVariant(iA)).ToList(),
                ReturnValue = Variant.Null
            };

            Invoke(method);
        }

        public T Invoke<T>(string name, params object[] parameters)
        {
            var method = new RemoteMethod
            {
                Name = name,
                InputArguments = parameters.Select(iA => TypeMapping.ToVariant(iA)).ToList(),
                ReturnValue = TypeMapping.MapType<T>()
            };

            var returnValue = Invoke(method);
            return (T) TypeMapping.ToObject(returnValue);
        }

        public void Write(string name, object parameter)
        {
            try
            {
                var variable = new RemoteVariable
                {
                    Name = name,
                    Value = TypeMapping.ToVariant(parameter)
                };

                variable.Write(this);
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, $"Cannot write {Name}.{name} without errors!");
            }
        }

        public T Read<T>(string name)
        {
            try
            {
                var variable = new RemoteVariable
                {
                    Name = name,
                    Value = TypeMapping.MapType<T>()
                };

                var result = variable.Read(this);
                return (T) TypeMapping.ToObject(result);
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, $"Cannot read {Name}.{name} without errors!");
            }

            return (T) TypeMapping.ToObject(TypeMapping.MapType<T>());
        }

        private Variant Invoke(RemoteMethod method)
        {
            try
            {
                Variant result = method.Invoke(this);
                return result;
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, $"Cannot invoke {Name}.{method.Name}() without errors!");
            }

            return method.HasReturnValue() ? method.ReturnValue : Variant.Null;
        }

        private object SessionLock { get; set; }

        public Variant Execute(Func<Variant> action)
        {
            if (!Connected())
            {
                throw new Exception("Cannot execute given client action, due to an unavailable connection!");
            }

            lock (SessionLock)
            {
                try
                {
                    return action();
                }
                catch (Exception e)
                {
                    ExceptionHandler.Log(e, $"Error while invoke something on '{Name}'.");
                    throw;
                }
            }
        }
    }
}