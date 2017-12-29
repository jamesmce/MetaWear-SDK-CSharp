﻿using MbientLab.MetaWear.Builder;
using MbientLab.MetaWear.Sensor;
using MbientLab.MetaWear.Core;
using MbientLab.MetaWear.Peripheral;
using MbientLab.MetaWear.Platform;
using static MbientLab.MetaWear.Impl.Module;

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Specialized;

namespace MbientLab.MetaWear.Impl {
    /// <summary>
    /// Implmementation of the <see cref="IMetaWearBoard"/> interface by MbientLab
    /// </summary>
    public class MetaWearBoard : IMetaWearBoard {
        internal const int COMMAND_LENGTH = 18, MAX_PACKET_LENGTH = COMMAND_LENGTH + 2;
        private static readonly Tuple<Guid, Guid> COMMAND_GATT_CHAR = new Tuple<Guid, Guid>(
            Constants.METAWEAR_GATT_SERVICE,
            new Guid("326A9001-85CB-9195-D9DD-464CFBBAE75A")
        ), NOTIFY_CHAR = new Tuple<Guid, Guid>(
            Constants.METAWEAR_GATT_SERVICE,
            new Guid("326A9006-85CB-9195-D9DD-464CFBBAE75A")
        );
        private static DataContractSerializerSettings SERIALIZE_SETTINGS = new DataContractSerializerSettings {
            PreserveObjectReferences = true,
            KnownTypes = new Type[] {
                typeof(AccelerometerBma255),
                typeof(AccelerometerBmi160),
                typeof(AccelerometerBosch),
                typeof(AccelerometerMma8452q),
                typeof(AmbientLightLtr329),
                typeof(BarometerBme280),
                typeof(BarometerBmp280),
                typeof(BarometerBosch),
                typeof(ByteArrayDataType),
                typeof(ColorTcs34725),
                typeof(DataProcessor),
                typeof(Debug),
                typeof(Event),
                typeof(Gpio),
                typeof(GyroBmi160),
                typeof(Haptic),
                typeof(HumidityBme280),
                typeof(IBeacon),
                typeof(Led),
                typeof(Logging),
                typeof(Macro),
                typeof(MagnetometerBmm150),
                typeof(NeoPixel),
                typeof(ProximityTsl2671),
                typeof(SensorFusionBosch),
                typeof(SerialPassthrough),
                typeof(Settings),
                typeof(Switch),
                typeof(Temperature),
                typeof(Timer)
            }
        };

        [DataContract]
        private class Observer : SerializableType, IObserver {
            [DataMember] private readonly uint id;
            [DataMember] private readonly Queue<byte> eventCmdIds;
            [DataMember] private bool valid;

            public uint ID {
                get {
                    return id;
                }
            }

            public bool Valid => valid;

            public Observer(uint id, Queue<byte> eventCmdIds, IModuleBoardBridge bridge) : base(bridge) {
                this.id = id;
                this.eventCmdIds = eventCmdIds;
                valid = true;
            }

            public void Remove() {
                Remove(true);
            }

            internal void Remove(bool sync) {
                if (valid) {
                    valid = false;

                    if (sync) {
                        bridge.removeObserver(id);

                        Event eventModule = bridge.GetModule<Event>();
                        foreach (byte it in eventCmdIds) {
                            eventModule.remove(it);
                        }
                    }
                }
            }
        }

        [KnownType(typeof(ScheduledTask))]
        [DataContract]
        private class Timer : ModuleImplBase, IModule {
            private const byte TIMER_ENTRY = 2,
                START = 3, STOP = 4, REMOVE = 5,
                NOTIFY = 6, NOTIFY_ENABLE = 7;

            [DataContract]
            internal class ScheduledTask : SerializableType, IScheduledTask {
                [DataMember] private bool _Valid = true;
                [DataMember] private readonly byte id;
                [DataMember] private Queue<byte> eventCmdIds;

                public bool Valid {
                    get {
                        return _Valid;
                    }
                }

                public byte ID {
                    get {
                        return id;
                    }
                }

                internal ScheduledTask(byte id, Queue<byte> eventCmdIds, IModuleBoardBridge bridge) : base(bridge) {
                    this.id = id;
                    this.eventCmdIds = eventCmdIds;
                }

                public void Remove() {
                    Remove(true);
                }

                internal void Remove(bool sync) {
                    if (Valid) {
                        _Valid = false;

                        if (sync) {
                            Timer timer = bridge.GetModule<Timer>();
                            timer.activeTasks[id] = null;

                            bridge.sendCommand(new byte[] { (byte) TIMER, REMOVE, id });

                            Event eventModule = bridge.GetModule<Event>();
                            foreach(byte it in eventCmdIds) {
                                eventModule.remove(it);
                            }
                        }
                    }
                }

                public void Start() {
                    if (Valid) {
                        bridge.sendCommand(new byte[] { (byte) TIMER, START, id });
                    }
                }

                public void Stop() {
                    if (Valid) {
                        bridge.sendCommand(new byte[] { (byte)TIMER, STOP, id });
                    }
                }
            }

            [DataMember] internal ScheduledTask[] activeTasks = null;

            private TimedTask<byte> createTimerTask;

            public Timer(IModuleBoardBridge bridge) : base(bridge) {
                activeTasks = new ScheduledTask[bridge.lookupModuleInfo(TIMER).extra[0]];
            }

            internal override void restoreTransientVars(IModuleBoardBridge bridge) {
                base.restoreTransientVars(bridge);

                foreach(var it in activeTasks) {
                    if (it != null) {
                        it.restoreTransientVars(bridge);
                    }
                }
            }

            protected override void init() {
                createTimerTask = new TimedTask<byte>();
                bridge.addRegisterResponseHandler(Tuple.Create((byte)TIMER, TIMER_ENTRY), response => createTimerTask.SetResult(response[2]));
            }

            public override void tearDown() {
                byte i = 0;
                foreach(var e in activeTasks) {
                    if (e != null) {
                        e.Remove(false);
                    }
                    bridge.sendCommand(new byte[] { (byte)TIMER, REMOVE, i });
                    i++;
                };
            }

            internal async Task<DataTypeBase> create(uint period, ushort repititions, bool delay) {
                byte[] cmd = new byte[9];
                cmd[0] = (byte)TIMER;
                cmd[1] = TIMER_ENTRY;
                cmd[8] = (byte)(delay ? 0 : 1);

                Array.Copy(Util.uintToBytesLe(period), 0, cmd, 2, 4);
                Array.Copy(Util.ushortToBytesLe(repititions), 0, cmd, 6, 2);

                var id = await createTimerTask.Execute("Did not receive timer ID within {0}ms", bridge.TimeForResponse,
                    () => bridge.sendCommand(cmd));

                return new IntegralDataType(TIMER, NOTIFY, id, new DataAttributes(new byte[] { }, 0, 0, false));
            }

            internal ScheduledTask createScheduledTask(byte id, Queue<byte> eventIds, IModuleBoardBridge bridge) {
                ScheduledTask newTask = new ScheduledTask(id, eventIds, bridge);
                activeTasks[id] = newTask;
                return newTask;
            }
        }

        private class ModuleBoardBridge : IModuleBoardBridge {
            private MetaWearBoard metawear;

            public int TimeForResponse => metawear.timeForResponse;

            public ModuleBoardBridge(MetaWearBoard metawear) {
                this.metawear = metawear;
            }
            
            public ModuleInfo lookupModuleInfo(Module module) {
                return metawear.persistent.attributes.moduleInfo.TryGetValue(module, out ModuleInfo result) ? result : null;
            }

            public async Task sendCommand(byte[] command) {
                if (GetModule<Event>() is Event eventModule && eventModule.ActiveDataType != null) {
                    eventModule.convertToEventCommand(command);
                } else {
                    try {
                        if (GetModule<IMacro>() is Macro macro && macro.isRecording) {
                            macro.commands.Enqueue(command);
                        }
                        await metawear.gatt.WriteCharacteristicAsync(
                            COMMAND_GATT_CHAR,
                            command[0] == (byte)MACRO ? GattCharWriteType.WRITE_WITH_RESPONSE : GattCharWriteType.WRITE_WITHOUT_RESPONSE,
                            command
                        );
                        
                    } catch (Exception e) {
                        metawear.io.LogWarn("metawear", "Failed to send command: " + Util.arrayToHexString(command), e);
                    }
                }
            }

            public async Task sendCommand(Module module, byte register, byte[] bytes) {
                byte[] command = new byte[bytes.Length + 2];

                command[0] = (byte) module;
                command[1] = register;
                Array.Copy(bytes, 0, command, 2, bytes.Length);

                await sendCommand(command);
            }

            public async Task sendCommand(Module module, byte register, byte id, byte[] bytes) {
                byte[] command = new byte[bytes.Length + 3];

                command[0] = (byte)module;
                command[1] = register;
                command[2] = id;
                Array.Copy(bytes, 0, command, 3, bytes.Length);

                await sendCommand(command);
            }
            public async Task sendCommand(byte[] command, byte dest, IDataToken input) {
                metawear.persistent.modules.TryGetValue(typeof(Event).FullName, out var module);
                Event eventModule = module as Event;

                DataTypeBase producer = (DataTypeBase)input;
                eventModule.feedbackParams= Tuple.Create(producer.attributes.length(), producer.attributes.offset, dest);
                await sendCommand(command);
                eventModule.feedbackParams= null;
            }
            public async Task sendCommand(byte dest, IDataToken input, Module module, byte register, byte id, params byte[] parameters) {
                byte[] command = new byte[parameters.Length + 3];
                Array.Copy(parameters, 0, command, 3, parameters.Length);
                command[0] = (byte) module;
                command[1] = register;
                command[2] = id;

                await sendCommand(command, dest, input);
            }

            public Task<IRoute> queueRouteBuilder(Action<IRouteComponent> builder, DataTypeBase source) {
                return metawear.queueRouteBuilderAsync(builder, source);
            }

            public async Task<IObserver> queueObserverAsync(Action commands, DataTypeBase dataSource) {
                var route = await queueRouteBuilder(source => source.React(token => commands()), dataSource) as Route;
                metawear.persistent.activeRoutes.Remove(route.ID);

                var observer = new Observer(route.ID, route.eventIds, this);
                metawear.persistent.activeObservers.Add(route.ID, observer);
                return await Task.FromResult<IObserver>(observer);
            }

            public void removeRoute(uint id) {
                metawear.persistent.activeRoutes.Remove(id);
            }

            public void removeObserver(uint id) {
                metawear.persistent.activeObservers.Remove(id);
            }

            public void registerProducerName(string name, DataTypeBase source) {
                metawear.persistent.namedProducers.Add(name, source);
            }

            public void removeProducerName(string name) {
                metawear.persistent.namedProducers.Remove(name);
            }

            public void addDataIdHeader(Tuple<byte, byte> key) {
                metawear.dataIdHeaders.Add(key);
            }

            public void addDataHandler(Tuple<byte, byte, byte> key, Action<byte[]> handler) {
                if (!metawear.dataHandlers.ContainsKey(key)) {
                    metawear.dataHandlers.Add(key, new HashSet<Action<byte[]>>());
                }

                metawear.dataHandlers.TryGetValue(key, out HashSet<Action<byte[]>> value);
                value.Add(handler);
            }

            public void removeDataHandler(Tuple<byte, byte, byte> key, Action<byte[]> handler) {
                if (metawear.dataHandlers.TryGetValue(key, out HashSet<Action<byte[]>>  value)) {
                    value.Remove(handler);
                }
            }

            public int numDataHandlers(Tuple<byte, byte, byte> key) {
                return metawear.dataHandlers.TryGetValue(key, out HashSet<Action<byte[]>>  value) ? value.Count : 0;
            }

            public T GetModule<T>() where T : class, IModule {
                return metawear.persistent.modules.TryGetValue(typeof(T).FullName, out IModule value) ? (T) value : null;
            }

            public void addRegisterResponseHandler(Tuple<byte, byte> key, Action<byte[]> handler) {
                metawear.registerResponseHandlers[key] = handler;
            }

            public Version getFirmware() {
                return metawear.persistent.attributes.firmware;
            }

            public ICollection<DataTypeBase> aggregateDataSources() {
                var seen = new HashSet<ModuleImplBase>();
                List<DataTypeBase> sources = new List<DataTypeBase>();
                foreach(var m in metawear.persistent.modules.Values) {
                    var casted = m as ModuleImplBase;
                    if (!seen.Contains(casted)) {
                        casted.aggregateDataType(sources);
                        seen.Add(casted);
                    }
                }

                return sources;
            }
        }

        [KnownType(typeof(ModuleInfo))]
        [KnownType(typeof(Version))]
        [DataContract]
        private class BoardAttributes {
            [DataMember] internal Dictionary<Module, ModuleInfo> moduleInfo = new Dictionary<Module, ModuleInfo>();
            [DataMember] internal Version firmware;
            [DataMember] internal String modelNumber, hardwareRevision;
        }

        [KnownType(typeof(DataTypeBase))]
        [KnownType(typeof(BoardAttributes))]
        [DataContract]
        private class Persistent {
            [DataMember] internal BoardAttributes attributes;
            [DataMember] internal uint id;

            [DataMember] internal Dictionary<uint, Observer> activeObservers = new Dictionary<uint, Observer>();
            [DataMember] internal Dictionary<uint, Route> activeRoutes= new Dictionary<uint, Route>();
            [DataMember] internal Dictionary<String, IModule> modules = new Dictionary<String, IModule>();
            [DataMember] internal Dictionary<String, DataTypeBase> namedProducers = new Dictionary<string, DataTypeBase>();

            internal Persistent() {
                id = 0;
            }
        }

        private const byte READ_INFO_REGISTER = 0x80;
        private const string BOARD_ATTR = "MbientLab.MetaWear.Impl.MetaWearBoard.BOARD_ATTR",
            BOARD_STATE = "MbientLab.MetaWear.Impl.MetaWearBoard.BOARD_STATE";

        private Persistent persistent = new Persistent();
        private String serialNumber = null, manufacturer = null;
        private IModuleBoardBridge bridge;
        private IBluetoothLeGatt gatt;
        private ILibraryIO io;
        private int timeForResponse;

        private HashSet<Tuple<byte, byte>> dataIdHeaders= new HashSet<Tuple<byte, byte>>();
        private Dictionary<Tuple<byte, byte, byte>, HashSet<Action<byte[]>>> dataHandlers= new Dictionary<Tuple<byte, byte, byte>, HashSet<Action<byte[]>>>();
        private Dictionary<Tuple<Byte, Byte>, Action<byte[]>> registerResponseHandlers= new Dictionary<Tuple<Byte, Byte>, Action<byte[]>>();

        public Action OnUnexpectedDisconnect { get; set; }
        public string MacAddress { get => gatt.BluetoothAddress.ToString("X").Insert(2, ":").Insert(5, ":").Insert(8, ":").Insert(11, ":").Insert(14, ":"); }
        public bool InMetaBootMode { get; private set; }
        public Model? Model {
            get {
                if (InMetaBootMode || persistent.attributes.moduleInfo.Count == 0 || persistent.attributes.modelNumber == null) {
                    return null;
                }

                if (persistent.attributes.modelNumber == "0") {
                    return MetaWear.Model.MetaWearR;
                }
                if (persistent.attributes.modelNumber == "1") {
                    if (!persistent.attributes.moduleInfo[AMBIENT_LIGHT].Present || !persistent.attributes.moduleInfo[BAROMETER].Present) {
                        return MetaWear.Model.MetaWearRG;
                    }
                    return MetaWear.Model.MetaWearRPro;
                }
                if (persistent.attributes.modelNumber == "2") {
                    if (persistent.attributes.moduleInfo[MAGNETOMETER].Present) {
                        return MetaWear.Model.MetaWearCPro;
                    }
                    switch(persistent.attributes.moduleInfo[ACCELEROMETER].implementation) {
                        case AccelerometerBmi160.IMPLEMENTATION:
                            return MetaWear.Model.MetaWearC;
                        case AccelerometerBma255.IMPLEMENTATION:
                            if (persistent.attributes.moduleInfo[PROXIMITY].Present) {
                                return MetaWear.Model.MetaDetect;
                            }
                            if (persistent.attributes.moduleInfo[HUMIDITY].Present) {
                                return MetaWear.Model.MetaEnv;
                            }
                            return null;
                    }
                }
                if (persistent.attributes.modelNumber == "3") {
                    return MetaWear.Model.MetaHealth;
                }
                if (persistent.attributes.modelNumber == "4") {
                    return MetaWear.Model.MetaTracker;
                }
                if (persistent.attributes.modelNumber == "5") {
                    return MetaWear.Model.MetaMotionR;
                }
                if (persistent.attributes.modelNumber == "6") {
                    return MetaWear.Model.MetaMotionC;
                }
                return null;
            }
        }
        public int TimeForResponse { set => timeForResponse = Math.Max(0, Math.Min(value, 3000)); }
        public bool IsConnected { get; private set; }

        public MetaWearBoard(IBluetoothLeGatt gatt, ILibraryIO io) {
            this.gatt = gatt;
            this.io = io;
            InMetaBootMode = false;
            timeForResponse = 250;            

            bridge = new ModuleBoardBridge(this);
            gatt.OnDisconnect = () => {
                IsConnected = false;

                var debug = GetModule<IDebug>() as Debug;
                var remoteDc = debug != null && debug.dcTaskSource != null;

                foreach (var it in persistent.modules.Values) {
                    (it as ModuleImplBase).disconnected();
                }

                if (!remoteDc && OnUnexpectedDisconnect != null) {
                    OnUnexpectedDisconnect();
                }
            };
        }

        public async Task<DeviceInformation> ReadDeviceInformationAsync() {
            if (serialNumber == null) {
                serialNumber = Encoding.ASCII.GetString(await gatt.ReadCharacteristicAsync(DeviceInformationService.SERIAL_NUMBER));
            }
            if (manufacturer == null) {
                manufacturer = Encoding.ASCII.GetString(await gatt.ReadCharacteristicAsync(DeviceInformationService.MANUFACTURER_NAME));
            }

            return new DeviceInformation(manufacturer, persistent.attributes.modelNumber, serialNumber, 
                persistent.attributes.firmware.ToString(), persistent.attributes.hardwareRevision);
        }

        public async Task<byte> ReadBatteryLevelAsync() {
            var result = await gatt.ReadCharacteristicAsync(BatteryService.BATTERY_LEVEL);
            return result[0];
        }

        public T GetModule<T>() where T : class, IModule {
            if (InMetaBootMode) {
                return null;
            }
            return persistent.modules.TryGetValue(typeof(T).FullName, out IModule module) ? (T)module : null;
        }

        public async Task<IDictionary> GetModuleInfoAsync(IDictionary partial) {
            IDictionary output = new SortedDictionary<String, IDictionary>();

            var ignore = new HashSet<Module>();
            if (partial != null) {
                foreach(var k in partial.Keys) {
                    output.Add(k, partial[k]);
                }

                foreach (Module module in Enum.GetValues(typeof(Module))) {
                    if (partial.Contains(Modules.FriendlyNames[module])) {
                        ignore.Add(module);
                    }
                }
            }

            Exception error = null;
            OrderedDictionary info = null;
            try {
                info = await DiscoverModulesAsync(ignore, null);
            } catch (TaskTimeoutException e) {
                error = e.InnerException;
                info = e.PartialResult as OrderedDictionary;
            } finally {
                foreach (var k in info.Keys) {
                    output.Add(Modules.FriendlyNames[(Module)k], (info[k] as ModuleInfo).Dump());
                }
            }

            if (error != null) {
                throw new TaskTimeoutException(error, output);
            }

            return output;
        }

        public async Task SerializeAsync() {
            using (MemoryStream outs = new MemoryStream()) {
                new DataContractSerializer(typeof(Persistent), SERIALIZE_SETTINGS).WriteObject(outs, persistent);
                await io.LocalSaveAsync(BOARD_STATE, outs.ToArray());
            }
        }

        public async Task DeserializeAsync() {
            using (Stream ins = await io.LocalLoadAsync(BOARD_STATE)) {
                persistent = new DataContractSerializer(typeof(Persistent), SERIALIZE_SETTINGS).ReadObject(ins) as Persistent;

                registerResponseHandlers.Clear();
                dataIdHeaders.Clear();
                dataHandlers.Clear();

                foreach (var it in persistent.activeObservers.Values) {
                    it.restoreTransientVars(bridge);
                }

                foreach (var it in persistent.activeRoutes.Values) {
                    it.restoreTransientVars(bridge);
                }

                foreach (var it in persistent.modules.Values) {
                    (it as SerializableType).restoreTransientVars(bridge);
                }
            }
        }

        public async Task<IScheduledTask> ScheduleAsync(uint period, ushort reptitions, bool delay, Action commands) {
            Timer timer = GetModule<Timer>();

            if (timer == null) {
                throw new NotSupportedException("Scheduling tasks not supported on this board / firmware");
            } else {
                var timerData = await timer.create(period, reptitions, delay);
                var route = await queueRouteBuilderAsync(source => source.React(token => commands()), timerData) as Route;
                persistent.activeRoutes.Remove(route.ID);

                return timer.createScheduledTask(timerData.eventConfig[2], route.eventIds, bridge);
            }
        }

        public Task<IScheduledTask> ScheduleAsync(uint period, bool delay, Action commands) {
            return ScheduleAsync(period, 0xff, delay, commands);
        }

        public IScheduledTask LookupScheduledTask(byte id) {
            return GetModule<Timer>().activeTasks[id];
        }

        private class AnonymousRoute : IAnonymousRoute {
            private readonly DeviceDataConsumer consumer;
            private readonly IModuleBoardBridge bridge;

            public string Identifier => consumer.source.CreateIdentifier(bridge);

            internal AnonymousRoute(DeviceDataConsumer consumer, IModuleBoardBridge bridge) {
                this.consumer = consumer;
                this.bridge = bridge;
            }

            public void Subscribe(Action<IData> subscriber) {
                consumer.subscriber = subscriber;
            }
        }

        public async Task<IList<IAnonymousRoute>> CreateAnonymousRoutesAsync() {
            var logging = GetModule<ILogging>();
            if (logging == null) {
                throw new NotSupportedException("On-board logging not available on this board or firmware");
            }

            var accelerometer = GetModule<IAccelerometer>();
            if (accelerometer != null) {
                await accelerometer.PullConfigAsync();
            }

            var gyro = GetModule<IGyroBmi160>();
            if (gyro != null) {
                await gyro.PullConfigAsync();
            }

            var sensorFusion = GetModule<ISensorFusionBosch>();
            if (sensorFusion != null) {
                await sensorFusion.PullConfigAsync();
            }

            return (await (logging as Logging).queryActiveLoggersAsync()).Select(e => new AnonymousRoute(e, bridge) as IAnonymousRoute).ToList();
        }

        public IObserver LookupObserver(uint id) {
            return persistent.activeObservers.TryGetValue(id, out var observer) ? observer : null;
        }

        public IRoute LookupRoute(uint id) {
            return persistent.activeRoutes.TryGetValue(id, out var route) ? route : null;
        }

        public void TearDown() {
            foreach (var it in persistent.activeRoutes.Values) {
                it.Remove(false);
            }

            foreach (var it in persistent.activeObservers.Values) {
                it.Remove(false);
            }

            foreach (var it in persistent.modules.Values) {
                (it as ModuleImplBase).tearDown();
            }

            persistent.id = 0;
            persistent.activeRoutes.Clear();
            persistent.activeObservers.Clear();
        }

#if NETSTANDARD2_0
        public async Task DisconnectAsync() {
            await gatt.DisconnectAsync();
        }
#endif

        public Task InitializeAsync()
        {
            return InitializeAsync(null);
        }

        public async Task InitializeAsync(Action<string, double> progress_cb) {
            if (persistent.attributes == null) {
                Stream ins = null;
                try {
                    ins = await io.LocalLoadAsync(BOARD_ATTR);
                    if (ins != null) {
                        persistent.attributes = new DataContractSerializer(typeof(BoardAttributes)).ReadObject(ins) as BoardAttributes;

                        foreach (var it in persistent.attributes.moduleInfo) {
                            instantiateModule(it.Key, it.Value);
                        }
                    } else {
                        persistent.attributes = new BoardAttributes();
                    }
                } catch (Exception e) {
                    io.LogWarn("metawear", "Could not deserialize board attributes", e);
                    persistent.attributes = new BoardAttributes();
                } finally {
                    if (ins != null) ins.Dispose();
                }
            }
            progress_cb("LoadLocal", 0.10);

            Exception error = null;
            try {
                await gatt.DiscoverServicesAsync();
                progress_cb("Services Discovered", 0.15);

                if (persistent.attributes.hardwareRevision == null) {
                    persistent.attributes.hardwareRevision = Encoding.ASCII.GetString(await gatt.ReadCharacteristicAsync(DeviceInformationService.HARDWARE_REVISION));
                }
                progress_cb("Loaded HW", 0.20);
                if (persistent.attributes.modelNumber == null) {
                    persistent.attributes.modelNumber = Encoding.ASCII.GetString(await gatt.ReadCharacteristicAsync(DeviceInformationService.MODEL_NUMBER));
                }
                progress_cb("Loaded Model#", 0.25);

                var firmware = new Version(Encoding.ASCII.GetString(await gatt.ReadCharacteristicAsync(DeviceInformationService.FIRMWARE_REVISION)));
                await gatt.EnableNotificationsAsync(NOTIFY_CHAR, value => {
                    Tuple<byte, byte> header = Tuple.Create(value[0], value[1]);
                    Tuple<byte, byte, byte> dataHandlerKey = Tuple.Create(value[0], value[1], dataIdHeaders.Contains(header) ? value[2] : DataTypeBase.NO_ID);

                    if (dataHandlers.TryGetValue(dataHandlerKey, out var handlers)) {
                        foreach (var handler in handlers) {
                            handler(value);
                        }
                    } else if (registerResponseHandlers.TryGetValue(header, out var handler)) {
                        handler(value);
                    } else if (value[1] == READ_INFO_REGISTER) {
                        readInfoRegisterTask.SetResult(new ModuleInfo(value));
                    }
                });
                InMetaBootMode = false;
                progress_cb("Notify Enabled", 0.30);

                if (persistent.attributes.firmware == null || persistent.attributes.firmware.CompareTo(firmware) != 0) {
                    persistent.id = 0;
                    persistent.activeObservers.Clear();
                    persistent.activeRoutes.Clear();
                    persistent.modules.Clear();
                    persistent.attributes.moduleInfo.Clear();

                    registerResponseHandlers.Clear();
                    dataIdHeaders.Clear();
                    dataHandlers.Clear();
                }
                persistent.attributes.firmware = firmware;

                var info = await DiscoverModulesAsync(persistent.attributes.moduleInfo.Keys, progress_cb);
                foreach (var it in info.Keys) {
                    persistent.attributes.moduleInfo.Add((Module)it, (ModuleInfo)info[it]);
                    instantiateModule((Module)it, (ModuleInfo)info[it]);
                }

                if (persistent.modules.TryGetValue(typeof(ILogging).FullName, out var logging)) {
                    await (logging as Logging).QueryTimeAsync();
                }

                using (MemoryStream outs = new MemoryStream()) {
                    new DataContractSerializer(typeof(BoardAttributes)).WriteObject(outs, persistent.attributes);
                    await io.LocalSaveAsync(BOARD_ATTR, outs.ToArray());
                }
            } catch (TaskTimeoutException e) {
                error = e.InnerException;
                var info = e.PartialResult as OrderedDictionary;
                foreach (var it in info.Keys) {
                    persistent.attributes.moduleInfo.Add((Module)it, (ModuleInfo)info[it]);
                    instantiateModule((Module)it, (ModuleInfo)info[it]);
                }
            } catch (Exception e) {
                InMetaBootMode = await gatt.ServiceExistsAsync(Constants.METABOOT_SERVICE);
                if (!InMetaBootMode) {
                    error = e;
                }
            } finally {
                if (error != null) {
                    throw error;
                }
                IsConnected = true;
            }
        }

        private TimedTask<ModuleInfo> readInfoRegisterTask;
        private async Task<OrderedDictionary> DiscoverModulesAsync(ICollection<Module> ignore, Action<string, double> progress_cb) {
            var output = new OrderedDictionary();
            readInfoRegisterTask = new TimedTask<ModuleInfo>();

            var i = 1;
            try {
                foreach (Module module in Enum.GetValues(typeof(Module))) {
                    if (!ignore.Contains(module)) {
                        var info = await readInfoRegisterTask.Execute("Did not receive module info (" + module.ToString() + ") within {0}ms", bridge.TimeForResponse,
                            () => bridge.sendCommand(new byte[] { (byte)module, READ_INFO_REGISTER }));
                        output.Add(module, info);

                        if (progress_cb != null)
                        {
                            progress_cb("Loaded: " + module.ToString(), 0.30 + 0.70 * (i++ / 13));

                        }
                    }
                }
            } catch (TimeoutException e) {
                throw new TaskTimeoutException(e, output);
            }

            return output;
        }
        private void instantiateModule(Module module, ModuleInfo info) {
            if (info.Present) {
                switch (module) {
                    case SWITCH:
                        persistent.modules.Add(typeof(ISwitch).FullName, new Switch(bridge));
                        break;
                    case LED:
                        persistent.modules.Add(typeof(ILed).FullName, new Led(bridge));
                        break;
                    case ACCELEROMETER:
                        IAccelerometer accelerometer = null;

                        switch (info.implementation) {
                            case AccelerometerMma8452q.IMPLEMENTATION:
                                accelerometer = new AccelerometerMma8452q(bridge);
                                persistent.modules.Add(typeof(IAccelerometerMma8452q).FullName, accelerometer);
                                break;
                            case AccelerometerBmi160.IMPLEMENTATION:
                                accelerometer = new AccelerometerBmi160(bridge);
                                persistent.modules.Add(typeof(IAccelerometerBosch).FullName, accelerometer);
                                persistent.modules.Add(typeof(IAccelerometerBmi160).FullName, accelerometer);
                                break;
                            case AccelerometerBma255.IMPLEMENTATION:
                                accelerometer = new AccelerometerBma255(bridge);
                                persistent.modules.Add(typeof(IAccelerometerBosch).FullName, accelerometer);
                                persistent.modules.Add(typeof(IAccelerometerBma255).FullName, accelerometer);
                                break;
                        }

                        if (accelerometer != null) {
                            persistent.modules.Add(typeof(IAccelerometer).FullName, accelerometer);
                        }
                        break;
                    case TEMPERATURE:
                        persistent.modules.Add(typeof(ITemperature).FullName, new Temperature(bridge));
                        break;
                    case GPIO:
                        persistent.modules.Add(typeof(IGpio).FullName, new Gpio(bridge));
                        break;
                    case NEO_PIXEL:
                        persistent.modules.Add(typeof(INeoPixel).FullName, new NeoPixel(bridge));
                        break;
                    case IBEACON:
                        persistent.modules.Add(typeof(IIBeacon).FullName, new IBeacon(bridge));
                        break;
                    case HAPTIC:
                        persistent.modules.Add(typeof(IHaptic).FullName, new Haptic(bridge));
                        break;
                    case DATA_PROCESSOR:
                        persistent.modules.Add(typeof(IDataProcessor).FullName, new DataProcessor(bridge));
                        break;
                    case EVENT:
                        persistent.modules.Add(typeof(Event).FullName, new Event(bridge));
                        break;
                    case LOGGING:
                        persistent.modules.Add(typeof(ILogging).FullName, new Logging(bridge));
                        break;
                    case TIMER:
                        persistent.modules.Add(typeof(Timer).FullName, new Timer(bridge));
                        break;
                    case SERIAL_PASSTHROUGH:
                        persistent.modules.Add(typeof(ISerialPassthrough).FullName, new SerialPassthrough(bridge));
                        break;
                    case MACRO:
                        persistent.modules.Add(typeof(IMacro).FullName, new Macro(bridge));
                        break;
                    case GSR:
                        break;
                    case SETTINGS:
                        persistent.modules.Add(typeof(ISettings).FullName, new Settings(bridge));
                        break;
                    case BAROMETER:
                        IBarometerBosch barometer = null;

                        switch (info.implementation) {
                            case BarometerBmp280.IMPLEMENTATION:
                                barometer = new BarometerBmp280(bridge);
                                persistent.modules.Add(typeof(IBarometerBmp280).FullName, barometer);
                                break;
                            case BarometerBme280.IMPLEMENTATION:
                                barometer = new BarometerBme280(bridge);
                                persistent.modules.Add(typeof(IBarometerBme280).FullName, barometer);
                                break;
                        }

                        if (barometer != null) {
                            persistent.modules.Add(typeof(IBarometerBosch).FullName, barometer);
                        }
                        break;
                    case GYRO:
                        persistent.modules.Add(typeof(IGyroBmi160).FullName, new GyroBmi160(bridge));
                        break;
                    case AMBIENT_LIGHT:
                        persistent.modules.Add(typeof(IAmbientLightLtr329).FullName, new AmbientLightLtr329(bridge));
                        break;
                    case MAGNETOMETER:
                        persistent.modules.Add(typeof(IMagnetometerBmm150).FullName, new MagnetometerBmm150(bridge));
                        break;
                    case HUMIDITY:
                        persistent.modules.Add(typeof(IHumidityBme280).FullName, new HumidityBme280(bridge));
                        break;
                    case COLOR_DETECTOR:
                        persistent.modules.Add(typeof(IColorTcs34725).FullName, new ColorTcs34725(bridge));
                        break;
                    case PROXIMITY:
                        persistent.modules.Add(typeof(IProximityTsl2671).FullName, new ProximityTsl2671(bridge));
                        break;
                    case SENSOR_FUSION:
                        persistent.modules.Add(typeof(ISensorFusionBosch).FullName, new SensorFusionBosch(bridge));
                        break;
                    case DEBUG:
                        persistent.modules.Add(typeof(IDebug).FullName, new Debug(bridge));
                        break;
                }
            }
        }

        // routes
        private readonly Queue<Tuple<Action<IRouteComponent>, RouteComponent, TaskCompletionSource<IRoute>>> pendingRoutes =
            new Queue<Tuple<Action<IRouteComponent>, RouteComponent, TaskCompletionSource<IRoute>>>();
        private async Task<IRoute> queueRouteBuilderAsync(Action<IRouteComponent> builder, DataTypeBase source) {
            TaskCompletionSource<IRoute> taskSource = new TaskCompletionSource<IRoute>();
            pendingRoutes.Enqueue(Tuple.Create(builder, new RouteComponent(source, bridge), taskSource));
            await createRouteAsync(false);
            return await taskSource.Task;
        }

        [KnownType(typeof(StreamedDataConsumer))]
        [KnownType(typeof(LoggedDataConsumer))]
        [DataContract]
        private class Route : SerializableType, IRoute {
            [DataMember] private readonly uint id;
            [DataMember] private readonly List<DeviceDataConsumer> consumers;
            [DataMember] private readonly Queue<byte> dataProcIds;
            [DataMember] internal readonly Queue<byte> eventIds;
            [DataMember] private readonly List<string> names;
            [DataMember] private bool valid;

            public uint ID => id;

            public bool Valid => valid;

            public Route(uint id, List<DeviceDataConsumer> consumers, Queue<byte> dataProcIds, Queue<byte> eventIds, List<string> names, IModuleBoardBridge bridge) : base(bridge) {
                this.id = id;
                this.consumers = consumers;
                this.dataProcIds = dataProcIds;
                this.eventIds = eventIds;
                this.names = names;
                valid = true;
            }

            internal override void restoreTransientVars(IModuleBoardBridge bridge) {
                base.restoreTransientVars(bridge);

                foreach (DeviceDataConsumer it in consumers) {
                    it.addDataHandler(bridge);
                }
            }

            public void Remove() {
                Remove(true);
            }

            internal void Remove(bool sync) {
                if (valid) {
                    valid = true;

                    if (sync) {
                        bridge.removeRoute(id);
                    }

                    foreach (var it in consumers) {
                        it.disableStream(bridge);

                        if (it is LoggedDataConsumer) {
                            (it as LoggedDataConsumer).remove(bridge, sync);
                        }
                    }

                    DataProcessor dataProc = bridge.GetModule<IDataProcessor>() as DataProcessor;
                    if (dataProcIds != null) {
                        foreach (var it in dataProcIds) {
                            dataProc.remove(it, sync);
                        }
                    }

                    if (sync) {
                        if (names != null) {
                            foreach (var it in names) {
                                dataProc.nameToId.Remove(it);
                                bridge.removeProducerName(it);
                            }
                        }

                        if (eventIds != null) {
                            Event eventModule = bridge.GetModule<Event>() as Event;
                            foreach (var it in eventIds) {
                                eventModule.remove(it);
                            }
                        }
                    }
                }
            }

            public bool AttachSubscriber(int pos, Action<IData> subscriber) {
                if (!valid) {
                    return false;
                }
                try {
                    consumers[pos].subscriber = subscriber;
                    return true;
                } catch (IndexOutOfRangeException) {
                    return false;
                }
            }

            public bool Resubscribe(int pos) {
                if (!valid) {
                    return false;
                }
                try {
                    consumers[pos].enableStream(bridge);
                    return true;
                } catch (IndexOutOfRangeException) {
                    return false;
                }
            }

            public bool Unsubscribe(int pos) {
                if (!valid) {
                    return false;
                }
                try {
                    consumers[pos].disableStream(bridge);
                    return true;
                } catch (IndexOutOfRangeException) {
                    return false;
                }
            }

            public string GenerateIdentifier(int pos) {
                try {
                    return consumers[pos].source.CreateIdentifier(bridge);
                } catch (IndexOutOfRangeException) {
                    return null;
                }
            }
        }
        private async Task createRouteAsync(bool ready) {
            if (pendingRoutes.Count != 0 && (ready || pendingRoutes.Count == 1)) {
                var top = pendingRoutes.Peek();

                try {
                    top.Item1(top.Item2);
                    foreach (var entry in top.Item2.state.namedProcessors) {
                        if (persistent.namedProducers.ContainsKey(entry.Key)) {
                            throw new IllegalRouteOperationException(string.Format("Duplicate producer name present: '{0}'", entry.Key));
                        }
                        persistent.namedProducers.Add(entry.Key, entry.Value.Item2.source);
                    }

                    Queue<byte> dataProcIds = null;
                    if (persistent.modules.TryGetValue(typeof(IDataProcessor).FullName, out var module)) {
                        DataProcessor dataProc = module as DataProcessor;
                        dataProcIds = await dataProc.queueDataProcessors(top.Item2.state.dataProcessors);
                        foreach (var entry in top.Item2.state.namedProcessors) {
                            if (entry.Value.Item2.source.eventConfig[0] == (byte)DATA_PROCESSOR) {
                                dataProc.nameToId.Add(entry.Key, entry.Value.Item2.source.eventConfig[2]);
                            }
                        }
                    }

                    Queue<LoggedDataConsumer> logConsumers = null;
                    if (persistent.modules.TryGetValue(typeof(ILogging).FullName, out module)) {
                        logConsumers = await (module as Logging).CreateLoggersAsync(top.Item2.state.subscribedProducers
                            .FindAll(p => p.Item3)
                            .Aggregate(new Queue<DataTypeBase>(), (acc, e) => {
                                acc.Enqueue(e.Item1);
                                return acc;
                            })
                        );
                    }

                    Queue<byte> eventIds = null;
                    if (persistent.modules.TryGetValue(typeof(Event).FullName, out module)) {
                        Event eventModule = module as Event;

                        var eventCodeBlocks = top.Item2.state.feedback.Aggregate(new Queue<Tuple<DataTypeBase, Action>>(), (acc, e) => {
                            if (!persistent.namedProducers.ContainsKey(e.Item1)) {
                                throw new IllegalRouteOperationException("\'" + e.Item1 + "\' is not associated with any data producer or named component");
                            }
                            var source = persistent.namedProducers[e.Item1];
                            acc.Enqueue(Tuple.Create<DataTypeBase, Action>(source, () => bridge.sendCommand(e.Item3, source, DATA_PROCESSOR, DataProcessor.PARAMETER, e.Item2.eventConfig[2], e.Item4)));

                            return acc;
                        });
                        top.Item2.state.reactions.ForEach(e => eventCodeBlocks.Enqueue(Tuple.Create<DataTypeBase, Action>(e.Item1, () => e.Item2(e.Item1))));

                        eventIds = await eventModule.queueEvents(eventCodeBlocks);
                    }

                    List<DeviceDataConsumer> consumers = new List<DeviceDataConsumer>();
                    //var enableTasks = top.Item2.state.subscribedProducers.Select(producer => { 
                    //top.Item2.state.subscribedProducers.ForEach(async producer => {
                    // Note: Don't use LINQ to trigger async actions.  It is equivalent
                    // to calling async void functions.
                    foreach (var producer in top.Item2.state.subscribedProducers) { 
                        if (logConsumers != null && producer.Item3) {
                            var logger = logConsumers.Dequeue();
                            logger.subscriber = producer.Item2;
                            consumers.Add(logger);
                        } else {
                            StreamedDataConsumer newConsumer = new StreamedDataConsumer(producer.Item1, producer.Item2);
                            await newConsumer.enableStream(bridge);
                            consumers.Add(newConsumer);
                        }
                    }

                    pendingRoutes.Dequeue();

                    Route newRoute = new Route(persistent.id, consumers, dataProcIds, eventIds,
                        top.Item2.state.namedProcessors.Count != 0 ? new List<string>(top.Item2.state.namedProcessors.Keys) : null, bridge);
                    persistent.activeRoutes.Add(persistent.id, newRoute);
                    persistent.id++;

                    top.Item3.SetResult(newRoute);
                } catch (Exception e) {
                    pendingRoutes.Dequeue();
                    top.Item3.SetException(e);
                } finally {
                    await createRouteAsync(true);
                }
            }
        }
    }
}
