﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using UaclUtils;
using UnifiedAutomation.UaBase;
using UnifiedAutomation.UaServer;

namespace UaclServer
{
    /**
     * The central class of the UA Server. You need to overwrite (or implement) some methods to have your own
     * behavior while starting and stopping the UA Server.
     *
     * So, it is a good idea to create your 'Server OPC UA Frontend' while server start. Means, create the UA Nodes,
     * relate your User Objects to it, and so on.
     *
     * While stopping the server you should cleanup your 'Server Side Objects'.
     */
    internal class InternalNodeManager : BaseNodeManager
    {
        public InternalNodeManager(ServerManager server, params string[] namespaceUris)
            : base(server, namespaceUris)
        {
            CompanyUri = namespaceUris.Length < 1 || namespaceUris[0].Trim().Length <= 0
                ? "http://www.company.com"
                : namespaceUris[0];

            ApplicationUri = namespaceUris.Length < 2 || namespaceUris[1].Trim().Length <= 0
                ? "application"
                : namespaceUris[1];

            InstanceNamespaceIndex = AddNamespaceUri($"{CompanyUri}/{ApplicationUri}/instances");
            TypeNamespaceIndex = AddNamespaceUri($"{CompanyUri}/{ApplicationUri}/types");
            _counter = 0;
            GlobalNotifier.LocalDataChangeEvent += (nodeId, variableName, value) =>
            {
                Logger.Trace($"LocalDataChangeEvent fired for varible {nodeId}={value}");
                server.InternalClient.WriteAttribute(server.DefaultRequestContext, nodeId, 13,
                    TypeMapping.ToVariant(value));
            };
        }

        private string ApplicationUri { get; set; }
        private string CompanyUri { get; set; }
        private ushort InstanceNamespaceIndex { get; set; }
        private ushort TypeNamespaceIndex { get; set; }


        private int IncrementCounter()
        {
            return ++_counter;
        }

        private int _counter;


        private InternalServerManager GetManager()
        {
            return (InternalServerManager) Server;
        }

        public override void Startup()
        {
            Logger.Info(@"InternalNodeManager: Startup()");
            CreateUaServerInterface();
            base.Startup();
        }

        private ObjectNode RootNode { get; set; }

        private void CreateUaServerInterface()
        {
            // The root folder, named by the specific application.
            RootNode = CreateObject(Server.DefaultRequestContext, new CreateObjectSettings
            {
                ParentNodeId = ObjectIds.ObjectsFolder,
                ReferenceTypeId = ReferenceTypeIds.Organizes,
                RequestedNodeId = new NodeId(ApplicationUri, InstanceNamespaceIndex),
                BrowseName = new QualifiedName(ApplicationUri, InstanceNamespaceIndex),
                TypeDefinitionId = ObjectTypeIds.FolderType,
                ParentAsOwner = true
            });
            Logger.Info($"Created root node ... {RootNode.NodeId.Identifier}.");

            // Here we add the registered objects. Unless, they aren't correctly annotated.
            foreach (var model in GetManager().BusinessModel)
            {
                AddNode(model, RootNode);
            }
        }

        public void AddUnregisteredNodes()
        {
            foreach (var model in GetManager().BusinessModel)
            {
                if (model.IsRegistered()) continue;
                AddNode(model, RootNode);
            }
        }

        private void AddNode(BoCapsule capsule, ObjectNode rootNode)
        {
            var businessObject = capsule.BoModel;

            var uaObject = businessObject.GetType().GetCustomAttribute<UaObject>();

            if (!capsule.IsRegistered())
            {
                var uaObjectName = uaObject.Name ?? businessObject.GetType().Name;
                var nodeIdName = $"{uaObjectName}_{IncrementCounter()}";
                capsule.BoId = new NodeId(nodeIdName, InstanceNamespaceIndex);

                var node = CreateObject(Server.DefaultRequestContext, new CreateObjectSettings
                {
                    ParentNodeId = rootNode.NodeId,
                    ReferenceTypeId = ReferenceTypeIds.Organizes,
                    RequestedNodeId = new NodeId(nodeIdName, InstanceNamespaceIndex),
                    BrowseName = new QualifiedName(uaObjectName, InstanceNamespaceIndex),
                    TypeDefinitionId = ObjectTypeIds.BaseObjectType,
                    ParentAsOwner = true
                });
                Logger.Info($"Created node ... {node.NodeId.Identifier}.");

                foreach (var property in businessObject.GetType().GetProperties())
                {
                    AddVariable(businessObject, property, node);
                    AddUaNodes(businessObject, property, node);
                }

                foreach (var method in businessObject.GetType().GetMethods())
                {
                    AddMethod(businessObject, method, node);
                }
            }
        }

        private void AddMethod(object businessObject, MethodInfo method, ObjectNode node)
        {
            var uaMethodAttribute = method.GetCustomAttribute<UaMethod>();
            if (uaMethodAttribute == null) return;

            var uaMethodName = uaMethodAttribute.Name ?? method.Name;
            var settings = new CreateMethodSettings()
            {
                ParentNodeId = node.NodeId,
                ReferenceTypeId = ReferenceTypeIds.HasComponent,
                RequestedNodeId = new NodeId($"{node.NodeId.Identifier}.{uaMethodName}", InstanceNamespaceIndex),
                BrowseName = new QualifiedName(uaMethodName, InstanceNamespaceIndex),
                DisplayName = uaMethodName,
                Executable = true,
                InputArguments = new List<Argument>(),
                OutputArguments = new List<Argument>(),
                ParentAsOwner = true
            };

            var parameterList = method.GetParameters().Select(p => p).ToList();
            if (ExistsInsertUaState(method))
            {
                parameterList.Remove(parameterList[0]);
            }

            foreach (var parameterInfo in parameterList)
            {
                settings.InputArguments.Add(new Argument
                {
                    DataType = TypeMapping.MapDataTypeId(parameterInfo.ParameterType),
                    Name = parameterInfo.Name,
                    Description = new LocalizedText("en", parameterInfo.Name),
                    ValueRank = ValueRanks.Scalar,
                });
            }

            if (method.ReturnParameter != null && method.ReturnParameter.ParameterType != typeof(void))
            {
                settings.OutputArguments.Add(new Argument
                {
                    DataType = TypeMapping.MapDataTypeId(method.ReturnParameter.ParameterType),
                    Name = "Result",
                    Description = new LocalizedText("en", "Result"),
                    ValueRank = ValueRanks.Scalar
                });
            }

            var methodNode = CreateMethod(Server.DefaultRequestContext, settings);
            methodNode.UserData = new MethodNodeData {BusinessObject = businessObject, Method = method};
            Logger.Info($"Created method ... {methodNode.NodeId.Identifier}.");
        }

        private void AddVariable(object businessObject, PropertyInfo property, ObjectNode parentNode)
        {
            var uaVariableAttribute = property.GetCustomAttribute<UaVariable>();
            if (uaVariableAttribute == null) return;
            var variableName = uaVariableAttribute.Name ?? property.Name;

            var requestedNodeId = new NodeId($"{parentNode.NodeId.Identifier}.{variableName}", InstanceNamespaceIndex);
            var variableNode = CreateVariable(Server.DefaultRequestContext,
                new CreateVariableSettings
                {
                    ParentNodeId = parentNode.NodeId,
                    ReferenceTypeId = ReferenceTypeIds.HasComponent,
                    RequestedNodeId = requestedNodeId,
                    BrowseName = new QualifiedName(variableName, InstanceNamespaceIndex),
                    DisplayName = variableName,
                    Description = new LocalizedText("en", variableName),
                    TypeDefinitionId = VariableTypeIds.BaseVariableType,
                    Value = new Variant("None"),
                    ParentAsOwner = true
                });

            var bo = businessObject as ServerSideUaProxy;
            if (bo != null)
            {
                bo.UniqueId[variableName] = requestedNodeId;
            }

            variableNode.UserData = new VariableNodeData {BusinessObject = businessObject, Property = property};

            Logger.Info($"Created variable ... {variableNode.NodeId.Identifier}.");
        }

        private void AddUaNodes(object businessObject, PropertyInfo property, ObjectNode parentNode)
        {
            var uaObjectListAttribute = property.GetCustomAttribute<UaObjectList>();
            if (uaObjectListAttribute == null) return;

            var uaNodeItems = property.GetValue(businessObject) as ICollection<object>;
            if (uaNodeItems == null) return;

            foreach (var o in uaNodeItems)
            {
                AddNode(new BoCapsule(o), parentNode);
            }
        }

        protected override CallMethodEventHandler GetMethodDispatcher(RequestContext context, MethodHandle methodHandle)
        {
            if (methodHandle.MethodData is MethodNodeData)
            {
                return DispatchControllerMethod;
            }
            return null;
        }

        private StatusCode DispatchControllerMethod(
            RequestContext context,
            MethodHandle methodHandle,
            IList<Variant> inputArguments,
            List<StatusCode> inputArgumentResults,
            List<Variant> outputArguments)
        {
            try
            {
                var data = methodHandle.MethodData as MethodNodeData;

                if (data == null) return StatusCodes.BadMethodInvalid;
                if (outputArguments.Count > 1) return StatusCodes.BadSyntaxError;
                var argumentCount = inputArguments.Count + (ExistsInsertUaState(data.Method) ? 1 : 0);
                if (data.Method.GetParameters().Length != argumentCount) return StatusCodes.BadNodeAttributesInvalid;

                var parameterList = new List<object>();
                if (ExistsInsertUaState(data.Method))
                {
                    object handler = null;
                    if (GetManager().GetSessionContext().ContainsKey(context.Session))
                    {
                        handler = GetManager().GetSessionContext()[context.Session];
                    }
                    parameterList.Add(handler);
                }
                parameterList.AddRange(inputArguments.Select(o => TypeMapping.ToObject(o)));

                var parameterArray = new object[parameterList.Count];
                for (var i = 0; i < parameterArray.Length; i++)
                {
                    parameterArray[i] = parameterList[i];
                }

                if (data.Method.ReturnType == typeof(void))
                {
                    ThreadPool.QueueUserWorkItem(
                        delegate { ExecuteMethodProtected(data, parameterArray); }, null);

                    return StatusCodes.Good;
                }

                var returnValue = ExecuteMethod(data, parameterArray);

                if (outputArguments.Count <= 0 || returnValue == null) return StatusCodes.Good;

                if (returnValue.GetType().IsArray)
                {
                    var returnValueDesc = new Variant(new byte[0]);
                    outputArguments[0] = returnValueDesc;
                }

                outputArguments[0] = TypeMapping.ToVariant(returnValue, outputArguments[0]);

                return StatusCodes.Good;
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, "Error while invoke a remote method ...");
                return StatusCodes.Bad;
            }
        }

        private static void ExecuteMethodProtected(MethodNodeData data, object[] parameterArray)
        {
            try
            {
                data.Method.Invoke(data.BusinessObject, parameterArray);
            }
            catch (Exception e)
            {
                ExceptionHandler.Log(e, "Error while invoke a remote method ...");
            }
        }

        private static object ExecuteMethod(MethodNodeData data, object[] parameterArray)
        {
            return data.Method.Invoke(data.BusinessObject, parameterArray);
        }


        private static bool ExistsInsertUaState(MethodInfo method)
        {
            var stateAttributes = method.GetCustomAttributes<UaclInsertState>();
            return stateAttributes != null && stateAttributes.Any();
        }

        protected override DataValue Read(
            RequestContext context,
            NodeAttributeHandle nodeHandle,
            string indexRange,
            QualifiedName dataEncoding)
        {
            DataValue baseValue = base.Read(context, nodeHandle, indexRange, dataEncoding);
            if (nodeHandle.AttributeId != 13) return baseValue;

            var processVariable = nodeHandle.UserData as VariableNode;
            var processVariableData = processVariable?.UserData as VariableNodeData;
            if (processVariableData == null) return baseValue;

            var v = new DataValue(processVariableData.ReadValue());

            return v;
        }

        protected override StatusCode? Write(
            RequestContext context,
            NodeAttributeHandle nodeHandle,
            string indexRange,
            DataValue value)
        {
            StatusCode? statusCode = base.Write(context, nodeHandle, indexRange, value);

            var processVariable = nodeHandle?.UserData as VariableNode;
            var processVariableData = processVariable?.UserData as VariableNodeData;
            if (processVariableData == null) return statusCode;

            return processVariableData.WriteValue(value.Value) &&
                   statusCode != null && StatusCode.IsGood((StatusCode) statusCode)
                ? StatusCodes.Good
                : StatusCodes.Bad;
        }

        public override void Shutdown()
        {
            base.Shutdown();
            Logger.Info(@"InternalNodeManager: Shutdown()");
        }

        public bool DeleteUaNode(NodeId id)
        {
            var sc = DeleteNode(Server.DefaultRequestContext, id, true);
            return sc.IsGood();
        }
    }
}