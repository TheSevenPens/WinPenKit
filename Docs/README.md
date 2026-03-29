# Readme

## Introduction
This is a collection of libraries and sample applications for receiving pen input from Wacom tablets. The core `PenSession` library provides a unified `IPenSession` interface across 7 backends (Wintab System, Wintab Digitizer, WM_Pointer, WinUI Pointer, WPF Stylus, Avalonia Pointer, WinForms Pointer), with 7 scribble apps across C#, C++, and Rust. The `ExtensionTestApp` demonstrates ExpressKey, Touch Ring, and Touch Strip data.

These demos show how applications can use the pen input APIs to:

* Connect to Wintab-enabled Wacom tablets.
* Open an application-private Wintab context and set up handlers for Pen, ExpressKey, Touch Ring, and Touch Strip events.
* Receive message notifications when tablet control data is available, as well as notifications of other tablet events (such as when a pen comes into proximity, etc.).
* Receive tablet information, usage, and status data for controls. 

To run these applications, a Wacom tablet driver must be installed and a supported Wacom tablet must be attached. All Wacom tablets supported by the Wacom driver are supported by this API. Get the driver that supports your device at: https://www.wacom.com/support/product-support/drivers.


## Application Details
The applications use an installed driver module Wintab32.dll and the project-built WintabDN.dll to communicate with the tablet driver. If the driver is not installed, is not communicating, or there is no supported Wacom tablet attached or in the preferences, then the program will display an appropriate warning.

The following illustrates a simplified overview of the major Wintab-supporting components:

![simplified overview of the major Wintab-supporting components](./Media/sc-wdn-rm-ad.png)

You can download the sample code and view the inline comments to find out detailed information about the sample code itself.


## See Also
[Wintab - Basics](https://developer-docs.wacom.com/intuos-cintiq-business-tablets/docs/wintab-basics) - How to configure and write Wintab applications  

[Wintab - Reference](https://developer-docs.wacom.com/intuos-cintiq-business-tablets/docs/wintab-reference) - Complete API details 

[Wintab - FAQs](https://developer-support.wacom.com/hc/en-us/articles/12844524637975-Wintab) - Wintab programming tips  



## Where To Get Help
If you have questions about this demo or the Wintab API, please visit our support page: https://developer.wacom.com/developer-dashboard/support.

 
## License
This sample code is licensed under the MIT License: https://choosealicense.com/licenses/mit/.