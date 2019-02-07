namespace SimuatedDevice
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Devices.Client;
    using System.Collections.Generic;     // For KeyValuePair<>
    using Microsoft.Azure.Devices.Shared; // For TwinCollection
    using Newtonsoft.Json;                // For JsonConvert
    using System.Diagnostics;
    class Program
    {
        static int counter;
        static String CONFIGURATION = "Configuration";
        static String ALGORITHM = "Algorithm";
        const String ALG_STABLE = "Stable";
        const String ALG_DECAY = "Decay";
        const String ALG_ERRATIC = "Erratic";
        static String DATA_FREQ = "Data_Freq";
        static String PARAMETERS = "Parameters";
        static String DECAY_RATE = "Decay_Rate";
        static String ERRATIC_FREQ = "Erratic_Freq";
        static String ERRATIC_COUNT = "Erratic_Count";
        static String NAME = "Name";
        static String PARAM_MIN = "Min";
        static String PARAM_MAX = "Max";
        static String PARAM_AVG = "Avg";
        static String PARAM_UOM = "Unit";
        static int defaultFreq = 60000; // 1 minute;
        static double defaultDecayRate = 0.01;

        static int defaultErraticCount = 2;
        static int defaultErraticFreq = 10;
        static SimlatorConfig config {get; set;}
        static Boolean reset = true;
        static Task runningThread = null;
        class SimlatorConfig
        {
            public String algorithm { get; set; }
            public int dataFreq { get; set; }
            public String parameters { get; set; }
            public double decayRate {get; set;}
            public int erraticFreq {get;set;}
            public int erraticCount {get; set;}
            public Dictionary<String, ParamValue> paramValues {get; set;}
            //public String active;
        }

        class ParamValue {
            public string name {get; set;}
            public double min {get; set;}
            public double max {get; set;}
            public double avg {get; set;}
            public String uom {get; set;}
        }

        class ParameterValue
        {
            public string Name { get; set; }
            public string Value { get; set; }
            public string Unit { get; set; }
            public string Status { get; set; }
            public string ValueType { get; set; }
        }

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            AmqpTransportSettings amqpSetting = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only);
            ITransportSettings[] settings = { amqpSetting };
            
            Console.WriteLine("ENV: " + JsonConvert.SerializeObject(Environment.GetEnvironmentVariables()));
            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");
           var moduleTwin = await ioTHubModuleClient.GetTwinAsync();
           Console.WriteLine("Init value: ");
           Console.WriteLine(JsonConvert.SerializeObject(moduleTwin));
            var moduleTwinCollection = moduleTwin.Properties.Desired;
            try {
                Console.WriteLine("Props: " + JsonConvert.SerializeObject(moduleTwinCollection));
                await OnDesiredPropertiesUpdate(moduleTwinCollection, ioTHubModuleClient);
            } catch(Exception e) {
                Console.WriteLine($"Property not exist: {e}"); 
            }
            
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertiesUpdate, ioTHubModuleClient);
            
            runningThread = Task.Run(()=>GenerateData(ioTHubModuleClient));
            // Attach a callback for updates to the module twin's desired properties.
            
            //GenerateData(ioTHubModuleClient);
            // Register a callback for messages that are received by the module.
            //await ioTHubModuleClient.SetInputMessageHandlerAsync("input1", GenerateData, ioTHubModuleClient);
            
        }

        public static double GetRandomNumber(double minimum, double maximum)
        { 
            Random random = new Random();
            return random.NextDouble() * (maximum - minimum) + minimum;
        }
        

        async static void GenerateData(object userContext) {
            Dictionary<string, string> paramCurrentValues = new Dictionary<string, string>();
        

            Console.WriteLine("########### STARTING SIMULATOR ################");
            int erraticCounter = 1;
            Boolean isErratic = false;
            ModuleClient moduleClient = (ModuleClient)userContext;
            Message message = null;
            while (true) {
                try {
                    if(config == null || config.algorithm == null || config.algorithm.Trim().Equals("")){
                        Console.WriteLine("Awaiting configuration... ");
                        Thread.Sleep(1000);
                    } else {
                        counter++;
                        var prevValue = paramCurrentValues;
                        paramCurrentValues = new Dictionary<string, string>();
                        //paramCurrentValues.Add("cycle", counter.ToString());
                        paramCurrentValues.Add("timestamp", DateTime.Now.ToString());
                        paramCurrentValues.Add("deviceName", Environment.GetEnvironmentVariable("IOTEDGE_DEVICEID"));
                        //paramCurrentValues.Add("moduleId", Environment.GetEnvironmentVariable("PUMP_ID"));
                        
                        if(reset) {
                            Console.WriteLine("########### RE-STARTING SIMULATOR"+(counter)+" ################");
                            foreach (String paramItem in config.paramValues.Keys) {
                                paramCurrentValues.Add(config.paramValues[paramItem].name, config.paramValues[paramItem].avg.ToString());
                            }
                            erraticCounter = 1;
                            isErratic = false;
                            Console.WriteLine("Current Values: " + JsonConvert.SerializeObject(paramCurrentValues));
                            message = createMessage(paramCurrentValues);
                            await moduleClient.SendEventAsync("output1", message);
                            reset = false;
                        } else {
                            Console.WriteLine("########### CONTINUING SIMULATOR"+(counter)+" ################");
                            switch(config.algorithm) {
                                case ALG_STABLE:
                                    
                                    Random rnd = new Random();
                                    List<ParameterValue> sensorList = new List<ParameterValue>();
                                    
                                    foreach (String paramItem in config.paramValues.Keys) {
                                        
                                        var sensorDataValues = new ParameterValue();
                                        ParamValue item = config.paramValues[paramItem];
                                        var ramdomSeed = GetRandomNumber(-0.09, 0.09);
                                        var value = (((item.max+item.min))/2)*(1+ramdomSeed);
                                        
                                        //Old changes
                                        //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Value"),value.ToString());               
                                        //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Unit"),config.paramValues[paramItem].uom.ToString());               

                                        //changes for new client
                                        sensorDataValues.Name=item.name;
                                        sensorDataValues.Value=value.ToString();
                                        sensorDataValues.Unit=item.uom;
                                        sensorDataValues.Status="Good";
                                        sensorDataValues.ValueType = null;
                                        sensorList.Add(sensorDataValues);
                                    }
                                    String parameterValues = JsonConvert.SerializeObject(sensorList);
                                    parameterValues.Replace("\"[","[");
                                    parameterValues.Replace("]\"","]");
                                    parameterValues.Replace("\\","");
                                    paramCurrentValues.Add("ParameterValues",parameterValues);

                                    Console.WriteLine("Current Values: " + JsonConvert.SerializeObject(paramCurrentValues));
                                    
                                    message = createMessage(paramCurrentValues);
                                    await moduleClient.SendEventAsync("output1", message);
                                    Console.WriteLine("message sent");
                                    break;
                                
                                case ALG_DECAY:

                                    Console.WriteLine("Previous Values: " + JsonConvert.SerializeObject(paramCurrentValues));
                                    List<ParameterValue> decaySensorList = new List<ParameterValue>();
                                    foreach (String paramItem in config.paramValues.Keys) {

                                        var sensorDataValues = new ParameterValue();
                                        ParamValue item = config.paramValues[paramItem];

                                        var value=0.0;
                                        Console.WriteLine("param: " + paramItem + " value: " + prevValue.GetValueOrDefault(config.paramValues[paramItem].name));
                                        if(prevValue.ContainsKey(config.paramValues[paramItem].name)) {
                                            value = Double.Parse(prevValue.GetValueOrDefault(config.paramValues[paramItem].name));    
                                        }
                                        else if(prevValue.ContainsKey(config.paramValues[paramItem].name.ToLower()+"Value")) {
                                            value = Double.Parse(prevValue.GetValueOrDefault(config.paramValues[paramItem].name.ToLower()+"Value"));
                                        }
                                        value = value-value*config.decayRate;
                                        if(value > config.paramValues[paramItem].min || value < config.paramValues[paramItem].max) {
                                           // paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Value"),value.ToString());               
                                            //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Unit"),config.paramValues[paramItem].uom.ToString());               
                                        sensorDataValues.Name=item.name;
                                        sensorDataValues.Value=value.ToString();
                                        sensorDataValues.Unit=item.uom;
                                        sensorDataValues.Status="Good";
                                        sensorDataValues.ValueType = null;
                                        decaySensorList.Add(sensorDataValues);
                                        }          
                                    }
                                    String decayParameterValues = JsonConvert.SerializeObject(decaySensorList);
                                    decayParameterValues.Replace("\"[","[");
                                    decayParameterValues.Replace("]\"","]");
                                    decayParameterValues.Replace("\\","");
                                    paramCurrentValues.Add("ParameterValues",decayParameterValues);
                                    Console.WriteLine("Current Values: " + JsonConvert.SerializeObject(paramCurrentValues));
                                    message = createMessage(paramCurrentValues);
                                    await moduleClient.SendEventAsync("output1", message);
                                    Console.WriteLine("message sent");
                                    break;

                                case ALG_ERRATIC:
                                    List<ParameterValue> erraticSensorList = new List<ParameterValue>();
                                    if(!isErratic) {
                                        foreach (String paramItem in config.paramValues.Keys) {

                                            var sensorDataValues = new ParameterValue();

                                            ParamValue item = config.paramValues[paramItem];
                                            var ramdomSeed = GetRandomNumber(-0.09, 0.09);
                                            var value = (((item.max+item.min))/2)*(1+ramdomSeed);
                                            
                                            //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Value"),value.ToString());               
                                            //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Unit"),config.paramValues[paramItem].uom.ToString());               

                                            sensorDataValues.Name=item.name;
                                            sensorDataValues.Value=value.ToString();
                                            sensorDataValues.Unit=item.uom;
                                            sensorDataValues.Status="Good";
                                            sensorDataValues.ValueType = null;
                                            erraticSensorList.Add(sensorDataValues);

                                        }
                                        erraticCounter++;
                                        if(erraticCounter > config.erraticFreq) {
                                            erraticCounter = 1;
                                            isErratic = true;
                                        }
                                    } else {
                                        foreach (String paramItem in config.paramValues.Keys) {

                                            var sensorDataValues = new ParameterValue();

                                            ParamValue item = config.paramValues[paramItem];
                                            var ramdomSeed = GetRandomNumber(0.01, 0.1);
                                            var value = item.min - ramdomSeed;
                                            //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Value"),value.ToString());               
                                            //paramCurrentValues.Add((((config.paramValues[paramItem].name).ToLower())+"Unit"),config.paramValues[paramItem].uom.ToString());               

                                            sensorDataValues.Name=item.name;
                                            sensorDataValues.Value=value.ToString();
                                            sensorDataValues.Unit=item.uom;
                                            sensorDataValues.Status="Good";
                                            sensorDataValues.ValueType = null;
                                            erraticSensorList.Add(sensorDataValues);

                                        }
                                        erraticCounter++;
                                        if(erraticCounter > config.erraticCount) {
                                            erraticCounter = 0;
                                            isErratic = false;
                                        }
                                    }
                                    String erraticParameterValues = JsonConvert.SerializeObject(erraticSensorList);
                                    erraticParameterValues.Replace("\"[","[");
                                    erraticParameterValues.Replace("]\"","]");
                                    erraticParameterValues.Replace("\\","");
                                    paramCurrentValues.Add("ParameterValues",erraticParameterValues);
                                    Console.WriteLine("Current Values: " + JsonConvert.SerializeObject(paramCurrentValues));
                                    message = createMessage(paramCurrentValues);
                                    await moduleClient.SendEventAsync("output1", message);
                                    Console.WriteLine("message sent");
                                    break;

                            }
                        }
                        Console.WriteLine("Waiting for: " +config.dataFreq+ "msec");
                        Thread.Sleep(config.dataFreq);
                    }
                } catch(Exception e) {
                    Console.WriteLine("Error occured: " + e);
                }
            } 
        }
        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
       static Task OnDesiredPropertiesUpdate(TwinCollection desiredProperties, object userContext)
        {
            try
            {
                SimlatorConfig newConfig = new SimlatorConfig();
                Boolean isValidConfig = true;
                Console.WriteLine("Desired property change:");
                Console.WriteLine(JsonConvert.SerializeObject(desiredProperties));
                Console.WriteLine("Configuring..");
                if(desiredProperties[CONFIGURATION] != null) {
                    Console.WriteLine("Configuration Present");
                    if(desiredProperties[CONFIGURATION][ALGORITHM] != null) {
                        newConfig.algorithm = desiredProperties[CONFIGURATION][ALGORITHM];
                        switch(newConfig.algorithm) {
                            case ALG_STABLE:
                                break;
                            case ALG_DECAY:
                                if(desiredProperties[CONFIGURATION][DECAY_RATE] != null) {
                                    newConfig.decayRate = desiredProperties[CONFIGURATION][DECAY_RATE];
                                } else {
                                    newConfig.decayRate = defaultDecayRate;
                                }
                                break;
                            case ALG_ERRATIC:
                                if(desiredProperties[CONFIGURATION][ERRATIC_COUNT] != null) {
                                    newConfig.erraticCount = desiredProperties[CONFIGURATION][ERRATIC_COUNT];
                                } else {
                                    newConfig.erraticCount = defaultErraticCount;
                                }
                                if(desiredProperties[CONFIGURATION][ERRATIC_FREQ] != null) {
                                    newConfig.erraticFreq = desiredProperties[CONFIGURATION][ERRATIC_FREQ];
                                } else {
                                    newConfig.erraticFreq = defaultErraticFreq;
                                }
                                break;
                            default:
                                isValidConfig = false;
                                throw new Exception("Unknown algorithm, supported algorithms: 'Stable','Decay', 'Erratic'");
                        }
                        if(desiredProperties[CONFIGURATION][DATA_FREQ] != null) {
                                    newConfig.dataFreq = desiredProperties[CONFIGURATION][DATA_FREQ];
                                } else {
                                    newConfig.dataFreq = defaultFreq;
                                }
                    } else {
                        isValidConfig = false;
                        throw new Exception("Algorithm for simultion is not specified in the Confiuration");
                    }
                    if(desiredProperties[CONFIGURATION][PARAMETERS] != null) {
                        Console.WriteLine("Parameters are not empty");
                        String[] paramsArr = ((string)desiredProperties[CONFIGURATION][PARAMETERS]).Split(",", StringSplitOptions.RemoveEmptyEntries);
                        Console.WriteLine("parameters count = x"+paramsArr.Length);
                        Dictionary<String, ParamValue> paramValueTemp = new Dictionary<String, ParamValue>();
                        foreach (String paramItem in paramsArr) {
                            ParamValue pValue = new ParamValue();
                            pValue.name = desiredProperties[CONFIGURATION][paramItem][NAME];
                            pValue.min = desiredProperties[CONFIGURATION][paramItem][PARAM_MIN];
                            pValue.max = desiredProperties[CONFIGURATION][paramItem][PARAM_MAX];
                            pValue.avg = desiredProperties[CONFIGURATION][paramItem][PARAM_AVG];
                            pValue.uom = desiredProperties[CONFIGURATION][paramItem][PARAM_UOM];
                            paramValueTemp.Add(paramItem, pValue);
                        }
                        newConfig.paramValues = paramValueTemp;
                    } else {
                        throw new Exception("The device returns no parameters");
                    }
                } else {
                    isValidConfig = false;
                    throw new Exception("Configution is missing for Device");
                }
                Console.WriteLine("Configuration Updated");
                Console.WriteLine("Old Config: " + JsonConvert.SerializeObject(config));
                if(isValidConfig) {
                    config = newConfig;
                    reset=true;
                }
                Console.WriteLine("Updated Config: " + JsonConvert.SerializeObject(config));
            }
            catch (AggregateException ex)
            {
                foreach (Exception exception in ex.InnerExceptions)
                {
                    Console.WriteLine();
                    Console.WriteLine("Error when receiving desired property: {0}", exception);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine("Error when receiving desired property: {0}", ex.Message);
            } 
            return Task.CompletedTask;
            
        }

        static Message createMessage (Dictionary<string, string> data)
        {
            var messageString = JsonConvert.SerializeObject(data);
            var messageBytes = Encoding.UTF8.GetBytes(messageString);
            var message = new Message(messageBytes);
            message.ContentEncoding = "utf-8"; 
            message.ContentType = "application/json"; 
            return message;
        }
    }
}
