﻿using HomeAutio.Mqtt.Core;
using HomeAutio.Mqtt.Core.Utilities;
using I8Beef.Ecobee;
using I8Beef.Ecobee.Protocol;
using I8Beef.Ecobee.Protocol.Functions;
using I8Beef.Ecobee.Protocol.Objects;
using I8Beef.Ecobee.Protocol.Thermostat;
using NLog;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Timers;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace HomeAutio.Mqtt.Apex
{
    public class EcobeeMqttService : ServiceBase
    {
        private ILogger _log = LogManager.GetCurrentClassLogger();

        private Client _client;
        private string _ecobeeName;

        private Timer _refresh;
        private int _refreshInterval;

        private IDictionary<string, RevisionStatus> _revisionStatusCache;
        private IDictionary<string, ThermostatStatus> _thermostatStatus;

        public EcobeeMqttService(Client ecobeeClient, string ecobeeName, int refreshInterval, string brokerIp, int brokerPort = 1883, string brokerUsername = null, string brokerPassword = null)
            : base(brokerIp, brokerPort, brokerUsername, brokerPassword, "ecobee/" + ecobeeName)
        {
            _refreshInterval = refreshInterval;
            _subscribedTopics = new List<string>();

            // /thermostatId/blah/set
            _subscribedTopics.Add(_topicRoot + "/+/+/set");

            _client = ecobeeClient;
            _ecobeeName = ecobeeName;
            _revisionStatusCache = new Dictionary<string, RevisionStatus>();
            _thermostatStatus = new Dictionary<string, ThermostatStatus>();
        }

        #region Service implementation

        /// <summary>
        /// Service Start action.
        /// </summary>
        public override void StartService()
        {
            GetInitialStatus();

            // Enable refresh
            if (_refresh != null)
                _refresh.Dispose();

            _refresh = new Timer();
            _refresh.Elapsed += RefreshAsync;
            _refresh.Interval = _refreshInterval;
            _refresh.Start();
        }

        /// <summary>
        /// Service Stop action.
        /// </summary>
        public override void StopService()
        {
            if (_refresh != null)
            {
                _refresh.Stop();
                _refresh.Dispose();
            }
        }

        #endregion

        #region MQTT Implementation

        /// <summary>
        /// Handles commands for the Ecobee published to MQTT.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void Mqtt_MqttMsgPublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            var message = Encoding.UTF8.GetString(e.Message);
            _log.Debug("MQTT message received for topic " + e.Topic + ": " + message);

            // Parse topic out
            var topicWithoutRoot = e.Topic.Substring(_topicRoot.Length + 1);
            var thermostatId = topicWithoutRoot.Substring(0, topicWithoutRoot.IndexOf('/'));
            var thermostatTopic = topicWithoutRoot.Substring(topicWithoutRoot.IndexOf('/') + 1);

            // ONLY VALID OPTIONS
            // HvacMode (auto, auxHeatOnly, cool, heat, off) - hvacMode/set
            // Fan (on, off, auto) - desiredFanMode/set
            // SetHold (hold, resume) - hold/set
            // Desired Heat - desiredHeat/set
            // Desired Cool - desiredCool/set

            // TODO: Figure out how to handle partial object requests
            var request = new ThermostatUpdateRequest
            {
                Selection = new Selection { SelectionType = "thermostat", SelectionMatch = thermostatId }
            };

            switch (thermostatTopic)
            {
                case "hvacMode/set":
                    request.Thermostat = new { Settings = new { HvacMode = message } };
                    _client.Post<ThermostatUpdateRequest, Response>(request);
                    break;
                case "desiredFanMode/set":
                    request.Thermostat = new { Runtime = new { DesiredFanMode = message } };
                    _client.Post<ThermostatUpdateRequest, Response>(request);
                    break;
                case "hold/set":
                    if (message == "hold")
                    {
                        // TODO: Figure out how to pass both desired heat and cool values at same time
                        var holdFunc = new SetHoldFunction();
                        ((SetHoldParams)holdFunc.Params).HoldType = "indefinite";
                        ((SetHoldParams)holdFunc.Params).CoolHoldTemp = 0;
                        ((SetHoldParams)holdFunc.Params).HeatHoldTemp = 0;
                        request.Functions.Add(holdFunc);
                        //_client.Post<ThermostatUpdateRequest, Response>(request);
                    }
                    else
                    {
                        var resumeFunc = new ResumeProgramFunction();
                        ((ResumeProgramParams)resumeFunc.Params).ResumeAll = true;
                        request.Functions.Add(resumeFunc);
                        _client.Post<ThermostatUpdateRequest, Response>(request);
                    }
                    break;
                case "desiredHeat/set":
                    if (int.TryParse(message, out int desiredHeatValue))
                    {
                        request.Thermostat = new { Runtime = new { DesiredHeat = desiredHeatValue * 100 } };
                        _client.Post<ThermostatUpdateRequest, Response>(request);
                    }
                    break;
                case "desiredCool/set":
                    if (int.TryParse(message, out int desiredCoolValue))
                    {
                        request.Thermostat = new { Runtime = new { DesiredCool = desiredCoolValue * 100 } };
                        _client.Post<ThermostatUpdateRequest, Response>(request);
                    }
                    break;
            }
        }

        #endregion

        #region Ecobee implementation

        /// <summary>
        /// Heartbeat ping. Failure will result in the heartbeat being stopped, which will 
        /// make any future calls throw an exception as the heartbeat indicator will be disabled.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void RefreshAsync(object sender, ElapsedEventArgs e)
        {
            // Get current revision status
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var status = await _client.Get<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest);
            foreach (var thermostatRevision in status.RevisionList)
            {
                var revisionStatus = new RevisionStatus(thermostatRevision);

                if (_revisionStatusCache[revisionStatus.ThermostatIdentifier].ThermostatRevision != revisionStatus.ThermostatRevision ||
                    _revisionStatusCache[revisionStatus.ThermostatIdentifier].RuntimeRevision != revisionStatus.RuntimeRevision)
                    RefreshThermostatAsync(revisionStatus);

                // Cache last run values
                _revisionStatusCache.Add(revisionStatus.ThermostatIdentifier, revisionStatus);
            }
        }

        /// <summary>
        /// Handles updating status for a thermostat and publishing changes.
        /// </summary>
        /// <param name="revisionStatus">Revision status that triggered the update.</param>
        private async void RefreshThermostatAsync(RevisionStatus revisionStatus)
        {
            // Build single thermostat request
            var thermostatRequest = new ThermostatRequest
            {
                Selection = new Selection
                {
                    SelectionType = "thermostat",
                    SelectionMatch = revisionStatus.ThermostatIdentifier,
                    IncludeEquipmentStatus = true,
                    IncludeSettings = true,
                    IncludeRuntime = true,
                    IncludeSensors = true
                }
            };

            var thermostatUpdate = await _client.Get<ThermostatRequest, ThermostatResponse>(thermostatRequest);

            // Publish updates and cache new values
            var thermostat = thermostatUpdate.ThermostatList.FirstOrDefault();
            if (thermostat != null)
            {
                var thermostatStatus = new ThermostatStatus();

                // Equipment status
                foreach (var device in thermostat.EquipmentStatus.Split(','))
                    thermostatStatus.EquipmentStatus[device] = true;

                // Status
                thermostatStatus.Status["hvacMode"] = thermostat.Settings.HvacMode;
                thermostatStatus.Status["humidifierMode"] = thermostat.Settings.HumidifierMode;
                thermostatStatus.Status["dehumidifierMode"] = thermostat.Settings.DehumidifierMode;
                thermostatStatus.Status["autoAway"] = thermostat.Settings.AutoAway.ToString();
                thermostatStatus.Status["vent"] = thermostat.Settings.Vent;
                thermostatStatus.Status["actualTemperature"] = thermostat.Runtime.ActualTemperature.ToString();
                thermostatStatus.Status["actualHumidity"] = thermostat.Runtime.ActualHumidity.ToString();
                thermostatStatus.Status["desiredHeat"] = thermostat.Runtime.DesiredHeat.ToString();
                thermostatStatus.Status["desiredCool"] = thermostat.Runtime.DesiredCool.ToString();
                thermostatStatus.Status["desiredHumidity"] = thermostat.Runtime.DesiredHumidity.ToString();
                thermostatStatus.Status["desiredDehumidity"] = thermostat.Runtime.DesiredDehumidity.ToString();
                thermostatStatus.Status["desiredFanMode"] = thermostat.Runtime.DesiredFanMode;

                // Sensors
                if (thermostat.RemoteSensors != null && thermostat.RemoteSensors.Count > 0)
                    foreach (var sensor in thermostat.RemoteSensors)
                        thermostatStatus.Sensors[sensor.Name] = sensor.Capability.ToDictionary(s => s.Type, s => s.Value);

                if (_thermostatStatus.ContainsKey(revisionStatus.ThermostatIdentifier))
                {
                    // Publish updates
                    foreach (var device in thermostatStatus.EquipmentStatus)
                        if (device.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].EquipmentStatus[device.Key])
                            _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/{device.Key}", Encoding.UTF8.GetBytes(device.Value.ToString()), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);

                    foreach (var status in thermostatStatus.Status)
                        if (status.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].Status[status.Key])
                            _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/{status.Key}", Encoding.UTF8.GetBytes(status.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);

                    // Publish everything for new sensors, new capabilities, and changes in existing ability values
                    foreach (var sensor in thermostatStatus.Sensors)
                        foreach (var sensorCapability in sensor.Value)
                            if (!_thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors.ContainsKey(sensor.Key))
                                _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensorCapability.Key}", Encoding.UTF8.GetBytes(sensorCapability.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
                            else if (!_thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors[sensor.Key].ContainsKey(sensorCapability.Key))
                                _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensorCapability.Key}", Encoding.UTF8.GetBytes(sensorCapability.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
                            else if (sensorCapability.Value != _thermostatStatus[revisionStatus.ThermostatIdentifier].Sensors[sensor.Key][sensorCapability.Key])
                                _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensorCapability.Key}", Encoding.UTF8.GetBytes(sensorCapability.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
                }
                else
                {
                    // Publish initial state
                    foreach (var device in thermostatStatus.EquipmentStatus)
                        _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/{device.Key}", Encoding.UTF8.GetBytes(device.Value.ToString()), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);

                    foreach (var status in thermostatStatus.Status)
                        _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/{status.Key}", Encoding.UTF8.GetBytes(status.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);

                    foreach (var sensor in thermostatStatus.Sensors)
                        foreach (var sensorCapability in sensor.Value)
                            _mqttClient.Publish($"{_topicRoot}/{revisionStatus.ThermostatIdentifier}/sensor/{sensorCapability.Key}", Encoding.UTF8.GetBytes(sensorCapability.Value), MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE, true);
                }

                _thermostatStatus[revisionStatus.ThermostatIdentifier] = thermostatStatus;
            }
        }

        /// <summary>
        /// Get, cache, and publish initial states.
        /// </summary>
        private void GetInitialStatus()
        {
            var summaryRequest = new ThermostatSummaryRequest { Selection = new Selection { SelectionType = "registered" } };
            var summary = _client.Get<ThermostatSummaryRequest, ThermostatSummaryResponse>(summaryRequest).GetAwaiter().GetResult();

            // Set initial revision cache
            _revisionStatusCache.Clear();
            _thermostatStatus.Clear();
            foreach (var revision in summary.RevisionList)
            {
                var revisionStatus = new RevisionStatus(revision);
                RefreshThermostatAsync(revisionStatus);
                _revisionStatusCache.Add(revisionStatus.ThermostatIdentifier, revisionStatus);
            }
        }

        #endregion
    }
}