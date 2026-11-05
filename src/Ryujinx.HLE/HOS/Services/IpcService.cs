using Gommon;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.HOS.Ipc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Ryujinx.HLE.HOS.Services
{
    abstract class IpcService
    {
        // Diagnostic: per-service-command call counts, printed with the NX2 progress line.
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> CallCounts = [];

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TipcSeen = new();

        /// <summary>Commands answered by a fake success because no handler exists.</summary>
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> MissingCalls = new();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> UnimplementedLogged = [];

        private static int _missingLogCount;

        public IReadOnlyDictionary<int, MethodInfo> CmifCommands { get; }
        public IReadOnlyDictionary<int, MethodInfo> TipcCommands { get; }

        public ServerBase Server { get; private set; }

        private IpcService _parent;
        private readonly IdDictionary _domainObjects;
        private int _selfId;
        private bool _isDomain;

        // cache array so we don't recreate it all the time
        private object[] _parameters = [null];

        public IpcService(ServerBase server = null, bool registerTipc = false)
        {
            Stopwatch sw = Stopwatch.StartNew();

            CmifCommands = GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                .SelectMany(methodInfo => methodInfo.GetCustomAttributes<CommandCmifAttribute>()
                .Select(command => (command.Id, methodInfo)))
                .ToDictionary(command => command.Id, command => command.methodInfo);

            sw.Stop();

            Logger.Debug?.Print(
                LogClass.Emulation,
                $"{CmifCommands.Count} Cmif commands loaded in {sw.ElapsedTicks} ticks ({Stopwatch.Frequency} tps).",
                GetType().AsPrettyString()
            );

            if (registerTipc)
            {
                sw.Start();

                TipcCommands = GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public)
                    .SelectMany(methodInfo => methodInfo.GetCustomAttributes<CommandTipcAttribute>()
                        .Select(command => (command.Id, methodInfo)))
                    .ToDictionary(command => command.Id, command => command.methodInfo);

                sw.Stop();

                Logger.Debug?.Print(
                    LogClass.Emulation,
                    $"{TipcCommands.Count} Tipc commands loaded in {sw.ElapsedTicks} ticks ({Stopwatch.Frequency} tps).",
                    GetType().AsPrettyString()
                );
            }

            Server = server;

            _parent = this;
            _domainObjects = new IdDictionary();
            _selfId = -1;
        }

        public int ConvertToDomain()
        {
            if (_selfId == -1)
            {
                _selfId = _domainObjects.Add(this);
            }

            _isDomain = true;

            return _selfId;
        }

        public void ConvertToSession()
        {
            _isDomain = false;
        }

        public void CallCmifMethod(ServiceCtx context)
        {
            IpcService service = this;

            if (_isDomain)
            {
                int domainWord0 = context.RequestData.ReadInt32();
                int domainObjId = context.RequestData.ReadInt32();

                int domainCmd = (domainWord0 >> 0) & 0xff;
                int inputObjCount = (domainWord0 >> 8) & 0xff;
                int dataPayloadSize = (domainWord0 >> 16) & 0xffff;

                context.RequestData.BaseStream.Seek(0x10 + dataPayloadSize, SeekOrigin.Begin);

                context.Request.ObjectIds.EnsureCapacity(inputObjCount);

                for (int index = 0; index < inputObjCount; index++)
                {
                    context.Request.ObjectIds.Add(context.RequestData.ReadInt32());
                }

                context.RequestData.BaseStream.Seek(0x10, SeekOrigin.Begin);

                if (domainCmd == 1)
                {
                    service = GetObject(domainObjId);

                    context.ResponseData.Write(0L);
                    context.ResponseData.Write(0L);
                }
                else if (domainCmd == 2)
                {
                    Delete(domainObjId);

                    context.ResponseData.Write(0L);

                    return;
                }
                else
                {
                    throw new NotImplementedException($"Domain command: {domainCmd}");
                }
            }

#pragma warning disable IDE0059 // Remove unnecessary value assignment
            long sfciMagic = context.RequestData.ReadInt64();
#pragma warning restore IDE0059
            int commandId = (int)context.RequestData.ReadInt64();

            CallCounts.AddOrUpdate($"{GetType().Name}:{commandId}", 1, (_, v) => v + 1);

            if (System.Environment.GetEnvironmentVariable("NX2_TIPC_LOG") == "1")
            {
                var cmifCaller = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();

                if (cmifCaller != null)
                {
                    string cmifKey = $"{GetType().Name}:{commandId}";
                    int n = TipcSeen.AddOrUpdate(cmifKey, 1, (_, v) => v + 1);

                    if (n == 1 || n % 500 == 0)
                    {
                        ulong clientPid = context.Request.HandleDesc is { HasPId: true } ? context.Request.HandleDesc.PId : 0;

                        Logger.Warning?.Print(LogClass.KernelIpc,
                            $"NX2 tipc {cmifKey} client={clientPid} server={context.Process.Pid} thread={cmifCaller.Owner.Pid}:{cmifCaller.ThreadUid}({cmifCaller.GetThreadName()}) n={n}");
                    }
                }
            }

            if (System.Environment.GetEnvironmentVariable("NX2_GSV_LOG") == "1" &&
                GetType().Name == "INx2GraphicsSystemSession")
            {
                int n = CallCounts[$"{GetType().Name}:{commandId}"];

                if (n == 1 || n % 500 == 0)
                {
                    Logger.Warning?.Print(LogClass.KernelIpc, $"NX2 gsvcall {commandId} n={n}");
                }
            }

            bool serviceExists = service.CmifCommands.TryGetValue(commandId, out MethodInfo processRequest);

            if (context.Device.Configuration.IgnoreMissingServices || serviceExists)
            {
                ResultCode result = ResultCode.Success;

                context.ResponseData.BaseStream.Seek(_isDomain ? 0x20 : 0x10, SeekOrigin.Begin);

                if (serviceExists)
                {
                    Logger.Trace?.Print(LogClass.KernelIpc, $"{service.GetType().Name}: {processRequest.Name}");

                    _parameters[0] = context;

                    result = (ResultCode)processRequest.Invoke(service, _parameters);
                }
                else
                {
                    string serviceName = (service is not DummyService dummyService) ? service.GetType().FullName : dummyService.ServiceName;

                    Logger.Debug?.Print(LogClass.KernelIpc, $"Missing service {serviceName}: {commandId} ignored");

                    // Diagnostic: show the payload of unimplemented commands once each, so a
                    // guest retry loop names the call it is missing.
                    string key = $"{service.GetType().Name}:{commandId}";

                    if (UnimplementedLogged.TryAdd(key, 0))
                    {
                        System.IO.Stream request = context.RequestData.BaseStream;
                        long saved = request.Position;
                        request.Position = 0;

                        int length = (int)Math.Min(request.Length, 0x40);
                        byte[] raw = new byte[length];

                        request.ReadExactly(raw, 0, length);
                        request.Position = saved;

                        string words = string.Join(' ', System.Linq.Enumerable.Select(
                            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw.AsSpan(0, length & ~3)).ToArray(),
                            w => w.ToString("X8")));

                        Logger.Warning?.Print(LogClass.KernelIpc, $"UNIMPLEMENTED {key} words=[{words}]");
                    }
                }

                if (_isDomain)
                {
                    foreach (int id in context.Response.ObjectIds)
                    {
                        context.ResponseData.Write(id);
                    }

                    context.ResponseData.BaseStream.Seek(0, SeekOrigin.Begin);

                    context.ResponseData.Write(context.Response.ObjectIds.Count);
                }

                context.ResponseData.BaseStream.Seek(_isDomain ? 0x10 : 0, SeekOrigin.Begin);

                context.ResponseData.Write(IpcMagic.Sfco);
                context.ResponseData.Write((long)result);
            }
            else
            {
                string dbgMessage = $"{service.GetType().FullName}: {commandId}";

                throw new ServiceNotImplementedException(service, context, dbgMessage);
            }
        }

        public void CallTipcMethod(ServiceCtx context)
        {
            int commandId = (int)context.Request.Type - 0x10;

            CallCounts.AddOrUpdate($"{GetType().Name}:{commandId}", 1, (_, v) => v + 1);

            if (System.Environment.GetEnvironmentVariable("NX2_TIPC_LOG") == "1")
            {
                var tipcCaller = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread();

                if (tipcCaller != null)
                {
                    string tipcKey = $"{GetType().Name}:{commandId}";
                    int n = TipcSeen.AddOrUpdate(tipcKey, 1, (_, v) => v + 1);

                    if (n == 1 || n % 500 == 0)
                    {
                        Logger.Warning?.Print(LogClass.KernelIpc,
                            $"NX2 tipc {tipcKey} server={context.Process.Pid} thread={tipcCaller.Owner.Pid}:{tipcCaller.ThreadUid}({tipcCaller.GetThreadName()}) n={n}");
                    }
                }
            }

            if (System.Environment.GetEnvironmentVariable("NX2_GSV_LOG") == "1" &&
                GetType().Name == "INx2GraphicsSystemSession")
            {
                int n = CallCounts[$"{GetType().Name}:{commandId}"];

                if (n == 1 || n % 500 == 0)
                {
                    Logger.Warning?.Print(LogClass.KernelIpc, $"NX2 gsvcall {commandId} n={n}");
                }
            }

            bool serviceExists = TipcCommands.TryGetValue(commandId, out MethodInfo processRequest);

            if (context.Device.Configuration.IgnoreMissingServices || serviceExists)
            {
                ResultCode result = ResultCode.Success;

                context.ResponseData.BaseStream.Seek(0x4, SeekOrigin.Begin);

                if (serviceExists)
                {
                    Logger.Debug?.Print(LogClass.KernelIpc, $"{GetType().Name}: {processRequest.Name}");

                    _parameters[0] = context;

                    result = (ResultCode)processRequest.Invoke(this, _parameters);
                }
                else
                {
                    string serviceName;

                    serviceName = (this is not DummyService dummyService) ? GetType().FullName : dummyService.ServiceName;

                    Logger.Debug?.Print(LogClass.KernelIpc, $"Missing service {serviceName}: {commandId} ignored");

                    // Silently answered correctly-looking is indistinguishable from working:
                    // count every command the emulator fakes, so a missing submission path is
                    // visible instead of looking like a guest that simply sends nothing.
                    MissingCalls.AddOrUpdate($"{GetType().Name}:{commandId}", 1, (_, v) => v + 1);

                    // Targeted probe (AGENTS.md section 9): dump request words for
                    // auto-stubbed commands so real handlers can be written. Env-gated,
                    // capped; log confinement via Nx2ProbeLog/Nx2LogConfinement.
                    if (System.Environment.GetEnvironmentVariable("NX2_MISSING_LOG") == "1" &&
                        System.Threading.Interlocked.Increment(ref _missingLogCount) <= 16)
                    {
                        try
                        {
                            long saved = context.RequestData.BaseStream.Position;
                            context.RequestData.BaseStream.Seek(0, SeekOrigin.Begin);
                            int words = (int)System.Math.Min(12, context.RequestData.BaseStream.Length / 4);
                            System.Text.StringBuilder sb = new();
                            for (int i = 0; i < words; i++)
                            {
                                sb.Append($"{context.RequestData.ReadUInt32():X8} ");
                            }
                            context.RequestData.BaseStream.Seek(saved, SeekOrigin.Begin);
                            var pc = Ryujinx.HLE.HOS.Kernel.KernelStatic.GetCurrentThread()?.Context.Pc ?? 0;
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.KernelIpc,
                                $"NX2 missing {GetType().Name}:{commandId} pc=0x{pc:X} req=[{sb}]");
                        }
                        catch (System.Exception ex)
                        {
                            Ryujinx.Common.Logging.Logger.Warning?.Print(Ryujinx.Common.Logging.LogClass.KernelIpc,
                                $"NX2 missing {GetType().Name}:{commandId} unreadable: {ex.GetType().Name}");
                        }
                    }
                }

                context.ResponseData.BaseStream.Seek(0, SeekOrigin.Begin);

                context.ResponseData.Write((uint)result);
            }
            else
            {
                string dbgMessage = $"{GetType().FullName}: {commandId}";

                throw new ServiceNotImplementedException(this, context, dbgMessage);
            }
        }

        protected void MakeObject(ServiceCtx context, IpcService obj)
        {
            obj.TrySetServer(_parent.Server);

            if (_parent._isDomain)
            {
                obj._parent = _parent;

                context.Response.ObjectIds.Add(_parent.Add(obj));
            }
            else
            {
                context.Device.System.KernelContext.Syscall.CreateSession(out int serverSessionHandle, out int clientSessionHandle, false, 0);

                obj.Server.AddSessionObj(serverSessionHandle, obj);

                context.Response.HandleDesc = IpcHandleDesc.MakeMove(clientSessionHandle);
            }
        }

        protected void MakeSessionObject(ServiceCtx context, IpcService obj)
        {
            obj.TrySetServer(Server);

            context.Device.System.KernelContext.Syscall.CreateSession(out int serverSessionHandle, out int clientSessionHandle, false, 0);

            Logger.Warning?.Print(LogClass.KernelIpc, $"MakeSessionObject: server={serverSessionHandle:X8} client={clientSessionHandle:X8}");

            Server.AddSessionObj(serverSessionHandle, obj);

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(clientSessionHandle);
        }

        protected T GetObject<T>(ServiceCtx context, int index) where T : IpcService
        {
            int objId = context.Request.ObjectIds[index];

            IpcService obj = _parent.GetObject(objId);

            return obj is T t ? t : null;
        }

        public bool TrySetServer(ServerBase newServer)
        {
            if (Server == null)
            {
                Server = newServer;

                return true;
            }

            return false;
        }

        private int Add(IpcService obj)
        {
            return _domainObjects.Add(obj);
        }

        private bool Delete(int id)
        {
            object obj = _domainObjects.Delete(id);

            if (obj is IDisposable disposableObj)
            {
                disposableObj.Dispose();
            }

            return obj != null;
        }

        private IpcService GetObject(int id)
        {
            return _domainObjects.GetData<IpcService>(id);
        }

        public void SetParent(IpcService parent)
        {
            _parent = parent._parent;
        }

        public virtual void DestroyAtExit()
        {
            foreach (object domainObject in _domainObjects.Values)
            {
                if (domainObject != this && domainObject is IDisposable disposableObj)
                {
                    disposableObj.Dispose();
                }
            }

            _domainObjects.Clear();
        }
    }
}
