Examples
========

LiquidWeighHMI
--------------

`LiquidWeighHMI.ahmi` is the operator interface of a liquid weigh-up system: three plasticizers are metered into a weigh hopper by weight, and each batch is discharged to a mixer. The [User Manual](../doc/Append-HMI-Studio-User-Manual.pdf) uses it throughout.

- **Screen:** 1920 × 1080, full screen.
- **Windows:** eight screens (Process, Batch, Supply, Devices, Setpoints, Recipes, Alarm History, IO Sim), and five popups: one Device Faceplate for all 11 valves and 3 pumps (through indirect tags) and 4 confirmations.
- **Data:** 452 tags: 419 on one ControlLogix controller (`LiquidWeighPLC`), 34 of them alarmed, plus memory and indirect tags.
- **Users:** Operator (level 1000), Maintenance (2000) and Engineer (9999).
- **Recipes:** the Plasticizer Blends recipe book and a Recipes screen.
- **Header:** every screen has PRINT SCREEN (`ScreenToPDF()`, a landscape PDF of the screen), the PLC and sequence status, the alarm count and LOGIN.

To try it:

1. Open it in Append HMI Studio (**File > Open**).
2. In **HMI > Devices**, set the controller's IP address and slot to yours.
3. **HMI > Run** (F5).

The controller program is not included. Without a controller holding the same tags, values show `####`. To explore the screens without one, change the device's protocol to **Simulator**.
