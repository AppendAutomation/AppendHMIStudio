Examples
========

LiquidWeighHMI
--------------

`LiquidWeighHMI.ahmi` is the operator interface of a liquid weigh-up system: three plasticizers are metered into a weigh hopper by weight, and each batch is discharged to a mixer. The [User Manual](../doc/Append-HMI-Studio-User-Manual.pdf) uses it throughout.

It was written from scratch by an AI agent, from the system's control narrative, I/O list and electrical drawings, after the agent had written the controller's PLC program from the same documents. See [HMI applications built by AI agents](../README.md#hmi-applications-built-by-ai-agents).

- **Screen:** 1920 × 1080, full screen.
- **Windows:** ten screens (Process, Batch, Supply, Devices, Setpoints, Recipes, Trends, Batch History, Alarm History, IO Sim) over the middle of the display, Header and Footer over the top and bottom strips, and five popups: one Device Faceplate for all 11 valves and 3 pumps (through indirect tags) and 4 confirmations. The faceplate is **movable**: at Run the operator drags its title bar to slide it off the device it covers.
- **Data:** 476 tags: 419 on one ControlLogix controller (`LiquidWeighPLC`), 34 of them alarmed, plus memory and indirect tags.
- **Users:** Operator (level 1000), Maintenance (2000) and Engineer (9999).
- **Recipes:** the Plasticizer Blends recipe book and a Recipes screen.
- **Trends:** a Trends screen with the weigh hopper's weight, target and pressure, the bulk tank level and three feed and discharge valves, and buttons that pick the pen and scroll the history through the `Trend` script functions.
- **Batch log:** every completed batch is written to a database. The data source **BatchDb** is a SQLite file, `LiquidWeighBatches.db`, in the Documents folder of the PC that runs the application, so no server and no password are needed. The **On start** application script connects and creates the table from the **BatchLogTable** template (a table that is already there is left alone); **While running** watches the batch counter and writes a row through the **BatchRow** bind list, stamped from the `_DateTime` system tag. The **Batch History** screen (the BATCH HISTORY button on the Batch screen) runs the project's **RecentBatches** query and walks the rows it found with `SQLFirst`, `SQLNext`, `SQLPrev` and `SQLLast`, a batch at a time. See [HMI_SQL.md](../doc/HMI_SQL.md).
- **Header and footer:** the header (PRINT SCREEN — `ScreenToPDF()`, a landscape PDF of the screen — the PLC and sequence status, the alarm count and LOGIN) and the navigation bar are overlay windows, open for the whole run. A screen covers only the middle strip, so changing screen rebuilds that alone. Each screen's On show script sets `ScreenTitle` and `ScreenNo`: the header shows the title and the footer highlights the screen's button.

To try it:

1. Open it in Append HMI Studio (**File > Open**).
2. In **HMI > Devices**, set the controller's IP address and slot to yours.
3. **HMI > Run** (F5).

The controller program is not included. Without a controller holding the same tags, values show `####`. To run the process without one, use LiquidWeighHMISim.

LiquidWeighHMISim
-----------------

`LiquidWeighHMISim.ahmi` is LiquidWeighHMI with no PLC: the same screens, users, recipe book and trend, with every PLC tag a memory tag and the controller program carried over into the application. Open it and press **HMI > Run** (F5); START runs a whole batch, and every button, faceplate and alarm works as it does with the controller.

- **The logic:** the application's **While running** script (HMI > Application Settings, every 100 ms) runs the controller's 13 ladder routines, rung for rung: defaults, process simulation, analogs, recipe, sequence, interlocks, valves, pumps, alarms and outputs, and then LiquidWeighHMI's own batch log. It runs whichever screen is up.
- **First scan:** the **On start** script loads the setpoint defaults on the first run and sets the simulation's starting values.
- **Local variables:** what the controller works out afresh on every scan (the step's first pass, the next step, flows, the flasher) is declared with `DIM` in the script rather than kept in tags.
- **Timers** count real time, measured each pass from the write time of `Sim_Clock`.
- **Simulation:** the IO Sim screen's Simulation switch, E-stop, loss of plant air, tote replacement and rates work as in the PLC. With Simulation OFF, click an input to drive it by hand.
- **Kept between runs:** setpoints, the recipe, valve travel and pump feedback times, and the batch count are retentive. **Load Defaults** on the Setpoints screen restores the defaults.
- **Batch log:** the same database as LiquidWeighHMI, and the quickest way to see it work: press START (or AUTO CYCLE) and the batches appear on the Batch History screen as they finish. The file is written to the Documents folder of whatever PC runs it, and can be opened with any SQLite tool while the application is running.
- **Header:** SIM PLC replaces PLC OK.

`tools/make_liquidweigh_sim.py` makes it from LiquidWeighHMI, so the two stay in step after the screens change:

```
npx electron . --hmi-dump examples/LiquidWeighHMI.ahmi -o lw.json      # from a copy outside the repository
python3 examples/tools/make_liquidweigh_sim.py lw.json sim.json
npx electron . --hmi-build sim.json -o examples/LiquidWeighHMISim.ahmi
```
