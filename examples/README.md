Examples
========

LiquidWeighHMI
--------------

`LiquidWeighHMI.ahmi` is the operator interface of a liquid weigh-up system: three plasticizers are metered into a weigh hopper by weight, and each batch is discharged to a mixer. The [User Manual](../doc/Append-HMI-Studio-User-Manual.pdf) uses it throughout.

- **Screen:** 1920 × 1080, full screen.
- **Windows:** seven screens (Process, Batch, Supply, Devices, Setpoints, Alarm History, IO Sim), and eighteen popups (faceplates for 11 valves and 3 pumps, and 4 confirmations).
- **Data:** 420 tags on one ControlLogix controller (`LiquidWeighPLC`), 34 of them alarmed.
- **Users:** Operator (level 1000), Maintenance (2000) and Engineer (9999).

To try it:

1. Open it in Append HMI Studio (**File > Open**).
2. In **HMI > Devices**, set the controller's IP address and slot to yours.
3. **HMI > Run** (F5).

The controller program is not included. Without a controller holding the same tags, values show `####`. To explore the screens without one, change the device's protocol to **Simulator**.
