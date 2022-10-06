using App.Helpers.EMVKernel;
using Common.Config.Config;
using Common.Constants;
using Common.LoggerManager;
using Common.XO.Device;
using Common.XO.Private;
using Common.XO.Requests;
using Common.XO.Responses;
using Devices.Common;
using Devices.Common.AppConfig;
using Devices.Common.Config;
using Devices.Common.Helpers;
using Devices.Common.Interfaces;
using Devices.Verifone.Connection;
using Devices.Verifone.Helpers;
using Devices.Verifone.VIPA;
using Devices.Verifone.VIPA.Helpers;
using Devices.Verifone.VIPA.Interfaces;
using Execution;
using Ninject;
using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using static Devices.Common.Constants.LogMessage;
using StringValueAttribute = Devices.Common.Helpers.StringValueAttribute;

namespace Devices.Verifone
{
    [Export(typeof(ICardDevice))]
    [Export("Verifone-M400", typeof(ICardDevice))]
    [Export("Verifone-P200", typeof(ICardDevice))]
    [Export("Verifone-P400", typeof(ICardDevice))]
    [Export("Verifone-UX300", typeof(ICardDevice))]
    internal class VerifoneDevice : IDisposable, ICardDevice
    {
        public string Name => StringValueAttribute.GetStringValue(DeviceType.Verifone);

        public event PublishEvent PublishEvent;
        public event DeviceEventHandler DeviceEventOccured;
        public event DeviceLogHandler DeviceLogHandler;

        private VerifoneConnection VerifoneConnection { get; set; }

        private (DeviceInfoObject deviceInfoObject, int VipaResponse) deviceVIPAInfo;
        private bool IsConnected { get; set; }

        DeviceConfig deviceConfiguration;
        DeviceSection deviceSectionConfig;

        [Inject]
        internal IVipa VipaConnection { get; set; } = new VIPAImpl();

        public IVipa VipaDevice { get; private set; }

        public AppExecConfig AppExecConfig { get; set; }

        public DeviceInformation DeviceInformation { get; private set; }

        public string ManufacturerConfigID => DeviceType.Verifone.ToString();

        public int SortOrder { get; set; } = -1;

        int ConfigurationHostId { get => deviceSectionConfig?.Verifone?.ConfigurationHostId ?? VerifoneSettingsSecurityConfiguration.ConfigurationHostId; }

        int OnlinePinKeySetId { get => deviceSectionConfig?.Verifone?.OnlinePinKeySetId ?? VerifoneSettingsSecurityConfiguration.OnlinePinKeySetId; }

        int ADEKeySetId { get => deviceSectionConfig?.Verifone?.ADEKeySetId ?? VerifoneSettingsSecurityConfiguration.ADEKeySetId; }

        string ConfigurationPackageActive { get => deviceSectionConfig?.Verifone?.ConfigurationPackageActive; }

        string SigningMethodActive { get; set; }

        string ActiveCustomerId { get => deviceSectionConfig?.Verifone?.ActiveCustomerId; }

        bool EnableHMAC { get; set; }

        LinkDALRequestIPA5Object VipaVersions { get; set; }

        public VerifoneDevice()
        {
            //string logsDir = Directory.GetCurrentDirectory() + Path.Combine("\\", LogDirectories.LogDirectory);
            //if (!Directory.Exists(logsDir))
            //{
            //    Directory.CreateDirectory(logsDir);
            //}

            // create dummy file to indicate task completion
            string targetDummyFile = Path.Combine(Constants.TargetDirectory, Constants.TargetDummyFile);
            if (!File.Exists(targetDummyFile))
            {
                File.Create(targetDummyFile);
            }

            //string pendingDir = Path.Combine(logsDir, LogDirectories.PendingDirectory);
            //if (!Directory.Exists(pendingDir))
            //{
            //    Directory.CreateDirectory(pendingDir);
            //}

            //string completedDir = Path.Combine(logsDir, LogDirectories.CompletedDirectory);
            //if (!Directory.Exists(completedDir))
            //{
            //    Directory.CreateDirectory(completedDir);
            //}
        }

        public object Clone()
        {
            VerifoneDevice clonedObj = new VerifoneDevice();
            return clonedObj;
        }

        public void Dispose()
        {
            VipaConnection?.Dispose();
            IsConnected = false;
        }

        public void Disconnect()
        {
            VerifoneConnection?.Disconnect();
            IsConnected = false;
        }

        bool ICardDevice.IsConnected(object request)
        {
            return IsConnected;
        }

        private IVipa LocateDevice(LinkDeviceIdentifier deviceIdentifer)
        {
            // If we have single device connected to the work station
            if (deviceIdentifer == null)
            {
                return VipaConnection;
            }

            // get device serial number
            string deviceSerialNumber = DeviceInformation?.SerialNumber;

            if (string.IsNullOrEmpty(deviceSerialNumber))
            {
                // clear up any commands the device might be processing
                //VipaConnection.AbortCurrentCommand();

                //SetDeviceVipaInfo(VipaConnection, true);
                //deviceSerialNumber = deviceVIPAInfo.deviceInfoObject?.LinkDeviceResponse?.SerialNumber;
            }

            if (!string.IsNullOrWhiteSpace(deviceSerialNumber))
            {
                // does device serial number match LinkDeviceIdentifier serial number
                if (deviceSerialNumber.Equals(deviceIdentifer.SerialNumber, StringComparison.CurrentCultureIgnoreCase))
                {
                    return VipaConnection;
                }
                else
                {
                    //VipaConnection.DisplayMessage(VIPADisplayMessageValue.Idle);
                }
            }

            return VipaConnection;
        }

        public void SetDeviceSectionConfig(DeviceSection config, AppExecConfig appConfig, bool displayOutput)
        {
            // L2 Kernel Information
            //int healthStatus = GetDeviceHealthStatus();

            //if (healthStatus == (int)VipaSW1SW2Codes.Success)
            //{
            //    ReportEMVKernelInformation();
            //}

            deviceSectionConfig = config;

            AppExecConfig = appConfig;

            // BUNDLE Signatures
            //GetBundleSignatures();

            SigningMethodActive = "UNSIGNED";

            //if (VipaVersions.DALCdbData is { })
            //{
            //    SigningMethodActive = VipaVersions.DALCdbData.VIPAVersion.Signature?.ToUpper() ?? "MISSING";
            //}
        }

        public List<LinkErrorValue> Probe(DeviceConfig config, DeviceInformation deviceInfo, out bool active)
        {
            DeviceInformation = deviceInfo;
            DeviceInformation.Manufacturer = ManufacturerConfigID;
            DeviceInformation.ComPort = deviceInfo.ComPort;

            // Updated config; disconnect any old connections
            VipaDevice?.Dispose();

            VerifoneConnection = new VerifoneConnection();
            active = IsConnected = VipaConnection.Connect(VerifoneConnection, DeviceInformation);

            if (active)
            {
                // update configuration
                VipaConnection.ConnectionConfiguration(config.SerialConfig, null, RaiseDeviceLog);

                (DeviceInfoObject deviceInfoObject, int VipaResponse) deviceIdentifier = VipaConnection.DeviceCommandReset();

                if (deviceIdentifier.VipaResponse == (int)VipaSW1SW2Codes.Success)
                {
                    // check for power on notification: reissue reset command to obtain device information
                    if (deviceIdentifier.deviceInfoObject.LinkDeviceResponse.PowerOnNotification != null)
                    {
                        Console.WriteLine($"\nDEVICE EVENT: Terminal ID={deviceIdentifier.deviceInfoObject.LinkDeviceResponse.PowerOnNotification?.TerminalID}," +
                            $" EVENT='{deviceIdentifier.deviceInfoObject.LinkDeviceResponse.PowerOnNotification?.TransactionStatusMessage}'");

                        deviceIdentifier = VipaConnection.DeviceCommandReset();

                        if (deviceIdentifier.VipaResponse != (int)VipaSW1SW2Codes.Success)
                        {
                            return null;
                        }
                    }

                    VipaDevice = VipaConnection;

                    if (DeviceInformation != null)
                    {
                        DeviceInformation.Manufacturer = ManufacturerConfigID;
                        DeviceInformation.Model = deviceIdentifier.deviceInfoObject.LinkDeviceResponse.Model;
                        DeviceInformation.SerialNumber = deviceIdentifier.deviceInfoObject.LinkDeviceResponse.SerialNumber;
                        DeviceInformation.FirmwareVersion = deviceIdentifier.deviceInfoObject.LinkDeviceResponse.FirmwareVersion;
                        DeviceInformation.VOSVersions = VipaDevice.DeviceInformation.VOSVersions;
                    }
                    VipaDevice = VipaConnection;
                    deviceConfiguration = config;
                    active = true;

                    //Console.WriteLine($"\nDEVICE PROBE SUCCESS ON {DeviceInformation?.ComPort}, FOR SN: {DeviceInformation?.SerialNumber}");
                }
                else
                {
                    //VipaDevice.CancelResponseHandlers();
                    //Console.WriteLine($"\nDEVICE PROBE FAILED ON {DeviceInformation?.ComPort}\n");
                }
            }
            return null;
        }

        protected internal void RaiseDeviceLog(LogLevel level, string message)
        {
            switch (level)
            {
                case LogLevel.Info:
                {
                    Logger.info(message);
                    break;
                }

                case LogLevel.Trace:
                {
                    Logger.info(message);
                    break;
                }

                case LogLevel.Debug:
                {
                    Logger.debug(message);
                    break;
                }

                case LogLevel.Warn:
                {
                    Logger.warning(message);
                    break;
                }

                case LogLevel.Error:
                {
                    Logger.error(message);
                    break;
                }

                case LogLevel.Critical:
                {
                    Logger.error(message);
                    break;
                }
            }
        }

        public List<DeviceInformation> DiscoverDevices()
        {
            List<DeviceInformation> deviceInformation = new List<DeviceInformation>();
            Connection.DeviceDiscovery deviceDiscovery = new Connection.DeviceDiscovery();
            if (deviceDiscovery.FindVerifoneDevices())
            {
                foreach (var device in deviceDiscovery.deviceInfo)
                {
                    if (string.IsNullOrEmpty(device.ProductID) || string.IsNullOrEmpty(device.SerialNumber))
                        throw new Exception("The connected device's PID or SerialNumber did not match with the expected values!");

                    deviceInformation.Add(new DeviceInformation()
                    {
                        ComPort = device.ComPort,
                        ProductIdentification = device.ProductID,
                        SerialNumber = device.SerialNumber,
                        VendorIdentifier = Connection.DeviceDiscovery.VID,
                        VOSVersions = new VOSVersions()
                    });

                    System.Diagnostics.Debug.WriteLine($"device: ON PORT={device.ComPort} - VERIFONE MODEL={deviceInformation[deviceInformation.Count - 1].ProductIdentification}, " +
                        $"SN=[{deviceInformation[deviceInformation.Count - 1].SerialNumber}], PORT={deviceInformation[deviceInformation.Count - 1].ComPort}");
                }
            }

            // validate COMM Port
            if (!deviceDiscovery.deviceInfo.Any() || deviceDiscovery.deviceInfo[0].ComPort == null || !deviceDiscovery.deviceInfo[0].ComPort.Any())
            {
                return null;
            }

            return deviceInformation;
        }

        public void DeviceSetIdle()
        {
            //Console.WriteLine($"DEVICE[{DeviceInformation.ComPort}]: SET TO IDLE.");
            if (VipaDevice != null)
            {
                VipaDevice.DisplayMessage(VIPAImpl.VIPADisplayMessageValue.Idle);
            }
        }

        public bool DeviceRecovery()
        {
            //Console.WriteLine($"DEVICE: ON PORT={DeviceInformation.ComPort} - DEVICE-RECOVERY");
            return false;
        }

        public List<LinkRequest> GetDeviceResponse(LinkRequest deviceInfo)
        {
            throw new NotImplementedException();
        }

        // ------------------------------------------------------------------------
        // Methods that are mapped for usage in their respective sub-workflows.
        // ------------------------------------------------------------------------
        #region --- subworkflow mapping
        public LinkRequest DisplayIdleScreen(LinkRequest linkRequest)
        {
            LinkActionRequest linkActionRequest = linkRequest?.Actions?.First();
            Console.WriteLine($"DEVICE: DISPLAY IDLE SCREEN COMMAND for SN='{linkActionRequest?.DeviceRequest?.DeviceIdentifier?.SerialNumber}'");
            return linkRequest;
        }

        public LinkActionRequest EnableADKLogger(LinkActionRequest linkActionRequest)
        {
            Console.WriteLine($"DEVICE[{DeviceInformation.ComPort}]: ENABLE ADK LOGGER for SN='{linkActionRequest?.DeviceRequest?.DeviceIdentifier?.SerialNumber}'");

            if (VipaDevice != null)
            {
                if (!IsConnected)
                {
                    VipaDevice.Dispose();
                    VerifoneConnection = new VerifoneConnection();
                    IsConnected = VipaDevice.Connect(VerifoneConnection, DeviceInformation);
                }

                if (IsConnected)
                {
                    // Push ADK Logger bundles to device
                    int vipaResponse = VipaDevice.EnableADKLogger(AppExecConfig.ADKLoggerContact, AppExecConfig.ADKLoggerContactless);

                    if (vipaResponse == (int)VipaSW1SW2Codes.Success)
                    {
                        Console.WriteLine($"DEVICE: ENABLE ADK LOGGER SUCCESSFULLY");
                        Console.Write("DEVICE: REQUESTING DEVICE REBOOT...");

                        try
                        {
                            VipaDevice.DeviceReboot();
                            RaiseDeviceLog(LogLevel.Info, "DEVICE: REBOOT REQUESTED.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"ENABLED ADK LOGGER COMMAND ERROR=[{ex.Message}]");
                        }
                    }
                    else
                    {
                        Console.WriteLine(string.Format("DEVICE: FAILED ENABLE ADK LOGGER REQUEST WITH ERROR=0x{0:X4}\n", vipaResponse));
                    }
                }
            }

            DeviceSetIdle();

            return linkActionRequest;
        }

        public LinkActionRequest ADKLoggerReset(LinkActionRequest linkActionRequest)
        {
            Console.WriteLine($"DEVICE[{DeviceInformation.ComPort}]: ADK LOGGER RESET for SN='{linkActionRequest?.DeviceRequest?.DeviceIdentifier?.SerialNumber}'");

            if (VipaDevice != null)
            {
                if (!IsConnected)
                {
                    VipaDevice.Dispose();
                    VerifoneConnection = new VerifoneConnection();
                    IsConnected = VipaDevice.Connect(VerifoneConnection, DeviceInformation);
                }

                if (IsConnected)
                {
                    // Reset ADK Logger
                    (DeviceInfoObject deviceInfoObject, int VipaResponse) deviceResponse = VipaDevice.ADKLoggerReset();

                    if (deviceResponse.VipaResponse == (int)VipaSW1SW2Codes.Success)
                    {
                        Console.WriteLine($"DEVICE: ADK LOGGER RESET SUCCESSFULLY");
                        if (deviceResponse.deviceInfoObject is { } infoObject)
                        {
                            Console.WriteLine($"VIPA: restart with message{infoObject.LinkDeviceResponse.PowerOnNotification.TransactionStatusMessage}");
                        }
                    }
                    else
                    {
                        Console.WriteLine(string.Format("DEVICE: FAILED ADK LOGGER RESET REQUEST WITH ERROR=0x{0:X4}\n", deviceResponse.VipaResponse));
                    }
                }
            }

            DeviceSetIdle();

            return linkActionRequest;
        }

        public LinkActionRequest GetTerminalLogs(LinkActionRequest linkActionRequest)
        {
            Console.WriteLine($"DEVICE[{DeviceInformation.ComPort}]: DUMP TERMINAL LOGS for SN='{linkActionRequest?.DeviceRequest?.DeviceIdentifier?.SerialNumber}'");

            if (VipaDevice != null)
            {
                if (!IsConnected)
                {
                    VipaDevice.Dispose();
                    VerifoneConnection = new VerifoneConnection();
                    IsConnected = VipaDevice.Connect(VerifoneConnection, DeviceInformation);
                }

                if (IsConnected)
                {
                    // OS log file
                    (BinaryStatusObject binaryStatusObject, int VipaResponse) deviceBinaryStatus = VipaDevice.DeviceDumpTerminalLogs();

                    if (deviceBinaryStatus.VipaResponse == (int)VipaSW1SW2Codes.Success)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"TERMINAL DUMP COMPLETED: {deviceBinaryStatus.binaryStatusObject.FileName}");
                        RaiseDeviceLog(LogLevel.Info, $"TERMINAL DUMP COMPLETED: {deviceBinaryStatus.binaryStatusObject.FileName}");
                    }
                }
            }

            DeviceSetIdle();

            return linkActionRequest;
        }

        public LinkActionRequest ReportVipaVersions(LinkActionRequest linkActionRequest)
        {
            Console.WriteLine($"DEVICE[{DeviceInformation.ComPort}]: REPORT VIPA VERSIONS for SN='{linkActionRequest?.DeviceRequest?.DeviceIdentifier?.SerialNumber}'");

            Console.WriteLine("");
            Console.WriteLine($"VIPA__: {DeviceInformation.FirmwareVersion}");
            Console.WriteLine($"VAULT_: {DeviceInformation.VOSVersions.ADKVault}");
            Console.WriteLine($"AppM__: {DeviceInformation.VOSVersions.ADKAppManager}");
            Console.WriteLine($"VFOPS_: {DeviceInformation.VOSVersions.ADKOpenProtocol}");
            Console.WriteLine($"VFSRED: {DeviceInformation.VOSVersions.ADKSRED}");

            if (VipaDevice != null)
            {
                if (!IsConnected)
                {
                    VipaDevice.Dispose();
                    VerifoneConnection = new VerifoneConnection();
                    IsConnected = VipaDevice.Connect(VerifoneConnection, DeviceInformation);
                }

                if (IsConnected)
                {
                    (DeviceInfoObject deviceInfoObject, int VipaResponse) deviceIdentifier = VipaDevice.DeviceCommandReset();

                    if (deviceIdentifier.VipaResponse == (int)VipaSW1SW2Codes.Success)
                    {
                        string style_pre = "<div style='font-family:monospace; font-size:16px;'>";
                        string message = $"<p>VIPA__: {DeviceInformation.FirmwareVersion}</p>";
                        message += $"<p>VAULT_: {DeviceInformation.VOSVersions.ADKVault}</p>";
                        message += $"<p>AppM__: {DeviceInformation.VOSVersions.ADKAppManager}</p>";
                        message += $"<p>VFOPS_: {DeviceInformation.VOSVersions.ADKOpenProtocol}</p>";
                        message += $"<p>VFSRED: {DeviceInformation.VOSVersions.ADKSRED}</p>";
                        string style_post = "</div>";
                        string displayMessage = style_pre + message + style_post;
                        (LinkDALRequestIPA5Object LinkActionRequestIPA5Object, int VipaResponse) showVipaVersionsResponse = VipaDevice.DisplayCustomScreenHTML(displayMessage);

                        if (showVipaVersionsResponse.VipaResponse == (int)VipaSW1SW2Codes.Success)
                        {
                            //    await Task.Delay(1000);
                            Thread.Sleep(10000);
                        }
                    }
                }
            }

            DeviceSetIdle();

            return linkActionRequest;
        }

        #endregion --- subworkflow mapping
    }
}
