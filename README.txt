===============================================================================
Verifone Terminal Logs Retrieval Tool
===============================================================================
Version : 1.0.0.0
Date    : 10/03/2022

This tool allows the user to retrieve terminal logs from a Verifone device and
it can operate in two modes as follows.

1. ADK LOGGER SETUP MODE

The device can be setup to report extended ADK logging when a contact
or contactless transaction needs further inspection.  In order to enable the ADK
extended logging option, set the items in the appsettings.json file:

    "ADKLoggerBundles": [
      "CONTACT",
      "CTLESS"
    ],
    "EnableADKLogger": true
    
One can choose to extend ADK logging for either CONTACT or
 CONTACTLESS transactions or both.
The tool will transfer a set of bundles to the device and a reboot will be
performed.  The "EnableADKLogger" option will be set to false so that the next
time the tool is launched, it will be set to retrieve terminal logs.

If multiple devices are connected to the workstation, set the "ComportBlacklList"
parameter to bypass communicating with the device(s) in the list:

"ComportBlacklList": [ "COM20" ]
"ComportBlacklList": [ "COM30", "COM40" ]
...

2. TERMINAL LOGS RETRIEVAL MODE (default)

With the "EnableADKLogger" parameter set to false, the tool will attempt to
retrieve terminal logs from the permitted device.  The logs will be placed in 
the in logs directory and the name format is "SN_DATE_TIME.tgz".

Example for P200 with serial number 275-437-650: "275-437-650_221003_101500.tgz".
