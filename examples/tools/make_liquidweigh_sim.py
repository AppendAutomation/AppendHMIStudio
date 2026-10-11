#!/usr/bin/env python3
"""Make LiquidWeighHMISim from LiquidWeighHMI.

LiquidWeighHMI reads and writes a ControlLogix program (LiquidWeigh.L5X).
LiquidWeighHMISim needs no PLC: every PLC tag becomes a memory tag, and the
controller's 13 ladder routines are carried over as the application's While
running script, run every 100 ms whichever screen is up. Its On start script
does what the PLC did on its first scan: the setpoint defaults (on the first
run only) and the simulation's starting values.

    python3 make_liquidweigh_sim.py <dump.json> <spec-out.json>

The input is `--hmi-dump` of LiquidWeighHMI.ahmi; build the output with
`--hmi-build <spec-out.json> -o LiquidWeighHMISim.ahmi`.

The script follows the PLC rung for rung. What differs:
- Timers count real milliseconds: each pass measures its own period from the
  write time of Sim_Clock (Dt, at most 1 s).
- What the PLC worked out afresh on every scan (the step's first pass, the
  next step, flows, the flasher ...) is a local variable of the script (DIM),
  not a tag: see LOCALS.
- Pushbuttons and device commands are used directly and cleared at the end
  of the pass (the PLC snapshots them first; a script cannot be interrupted
  by the HMI writing a tag, so the result is the same).
- A tag is written only when its value changes, because every write is sent
  to every window.
"""

import json
import re
import sys

VALVES = ['SV001', 'SV002', 'SV100', 'SV101', 'SV102', 'SV200', 'SV201', 'SV300', 'SV301', 'SV401', 'SV402']
NO_LIMITS = ['SV001', 'SV002']
PUMPS = ['P100', 'P200', 'P300']
# Devices that take part in the sequence (SV-102 and P-100 do not)
SEQ_DEVICES = ['SV001', 'SV002', 'SV100', 'SV101', 'SV200', 'SV201', 'SV300', 'SV301', 'SV401', 'SV402', 'P200', 'P300']
# The simulated valves' coils (SV-102 is wired through CR1)
SIM_VALVES = [('100', 'DO_SV_100'), ('101', 'DO_SV_101'), ('102', 'Sim_CoilSV102'), ('200', 'DO_SV_200'),
	('201', 'DO_SV_201'), ('300', 'DO_SV_300'), ('301', 'DO_SV_301'), ('401', 'DO_SV_401'), ('402', 'DO_SV_402')]
PUSHBUTTONS = ['PB_Start', 'PB_Hold', 'PB_Resume', 'PB_Abort', 'PB_Discharge', 'PB_EmptyHopper', 'PB_Reset',
	'PB_Silence', 'PB_RefillOpen', 'PB_RefillClose', 'PB_AcceptTol', 'PB_TopOff', 'PB_LoadDefaults',
	'PB_ResetCount', 'PB_AllAuto', 'PB_AllManual']

WHILE_RUNNING_EVERY_MS = '100'

# Tags of the PLC program that the script works out afresh on every pass,
# before it reads them: local variables (DIM), by name and type
LOCALS = [('Sim_Dt', 'Dt', 'Analog'), ('Sim_Flow1', 'Flow1', 'Analog'), ('Sim_Flow2', 'Flow2', 'Analog'),
	('Sim_Flow3', 'Flow3', 'Analog'), ('Sim_DischFlow', 'DischFlow', 'Analog'),
	('Sim_PressTarget', 'PressTarget', 'Analog'), ('Seq_NextStep', 'NextStep', 'Analog'),
	('Seq_ActStep', 'ActStep', 'Analog'), ('Sim_CoilSV102', 'CoilSV102', 'Discrete'),
	('Sim_DischOpen', 'DischOpen', 'Discrete'), ('Seq_StepInit', 'StepInit', 'Discrete'),
	('Seq_FillActive', 'FillActive', 'Discrete'), ('Seq_FillValvesClosed', 'FillValvesClosed', 'Discrete'),
	('Alm_Flash', 'Flash', 'Discrete')]
LOCAL_NAMES = {tag: name for tag, name, _ in LOCALS}


class Script:
	def __init__(self, locals_=None):
		self.lines = []
		self.depth = 0
		# Tag names written here as local variables
		self.locals = locals_ or {}

	def put(self, text):
		self.lines.append('\t' * self.depth + text)

	def note(self, text):
		assert '}' not in text and '{' not in text
		self.put('{ ' + text + ' }')

	def section(self, text):
		self.lines.append('')
		self.note(text)

	def begin(self, cond):
		self.put('IF ' + cond + ' THEN')
		self.depth += 1

	def otherwise(self):
		self.depth -= 1
		self.put('ELSE')
		self.depth += 1

	def end(self):
		self.depth -= 1
		self.put('ENDIF;')

	# Writes, each only when the value changes

	def coil(self, tag, cond):
		"""OTE: tag follows cond."""
		if tag in self.locals:
			self.put('%s = %s;' % (tag, cond))
			return
		self.begin(cond)
		self.put('IF NOT %s THEN %s = 1; ENDIF;' % (tag, tag))
		self.otherwise()
		self.put('IF %s THEN %s = 0; ENDIF;' % (tag, tag))
		self.end()

	def latch(self, tag, cond=None):
		"""OTL."""
		self.put('IF %s%s THEN %s = 1; ENDIF;' % ('(%s) AND ' % cond if cond else '', 'NOT ' + tag, tag))

	def unlatch(self, tag, cond=None):
		"""OTU."""
		self.put('IF %s%s THEN %s = 0; ENDIF;' % ('(%s) AND ' % cond if cond else '', tag, tag))

	def set(self, tag, expr):
		"""MOV / CPT."""
		if tag in self.locals:
			self.put('%s = %s;' % (tag, expr))
			return
		self.put('IF %s <> (%s) THEN %s = %s; ENDIF;' % (tag, expr, tag, expr))

	def ton(self, acc, dn, enable, pre):
		"""TON with its accumulator and done bit in tags, counting Sim_Dt."""
		self.begin(enable)
		self.put('IF %s < %s THEN %s = Min(%s + Sim_Dt, %s); ENDIF;' % (acc, pre, acc, acc, pre))
		self.coil(dn, '%s >= %s' % (acc, pre))
		self.otherwise()
		self.put('IF %s <> 0 THEN %s = 0; ENDIF;' % (acc, acc))
		self.put('IF %s THEN %s = 0; ENDIF;' % (dn, dn))
		self.end()

	def tof(self, acc, dn, enable, pre):
		"""TOF: done while enabled and until pre has passed since."""
		self.begin(enable)
		self.put('IF %s <> 0 THEN %s = 0; ENDIF;' % (acc, acc))
		self.put('IF NOT %s THEN %s = 1; ENDIF;' % (dn, dn))
		self.otherwise()
		self.put('IF %s < %s THEN %s = Min(%s + Sim_Dt, %s); ENDIF;' % (acc, pre, acc, acc, pre))
		self.coil(dn, '%s < %s' % (acc, pre))
		self.end()

	def text(self):
		"""The script, with its local variables declared first and called by their own names."""
		head = []
		for kind in ('Analog', 'Discrete'):
			names = [name for tag, name, k in LOCALS if k == kind and tag in self.locals]
			if names:
				head.append('DIM %s AS %s;' % (', '.join(names), kind))
		body = '\n'.join(self.lines)
		for tag in self.locals:
			body = re.sub(r'\b%s\b' % tag, self.locals[tag], body)
		return ('\n'.join(head) + '\n' if head else '') + body + '\n'


def any_of(names):
	return '(' + ' OR '.join(names) + ')'


def build_on_start():
	"""The PLC's first scan."""
	s = Script()
	s.note('LIQUID WEIGH SYSTEM: what the controller does on its first scan (LiquidWeighHMISim).')
	s.note('Generated by examples/tools/make_liquidweigh_sim.py. The While running script is the rest of the program.')
	s.section('R01 DEFAULTS: setpoints and recipe, on the first run only (they are kept from one run to the next)')
	s.begin('NOT Cfg_DefaultsLoaded')
	load_defaults(s)
	s.end()
	s.section('Simulation starting values, with simulation ON')
	s.put('Sim_Enable = 1;')
	sim_starting_values(s)
	return s.text()


def build_script():
	s = Script(LOCAL_NAMES)
	s.note('LIQUID WEIGH SYSTEM: the controller program, run by the HMI every 100 ms (LiquidWeighHMISim).')
	s.note('Generated by examples/tools/make_liquidweigh_sim.py from the ladder routines of LiquidWeigh.L5X.')
	s.note('The DIM variables above are worked out afresh on every pass; everything else is a tag.')

	s.section('Pass period (Dt, ms): Sim_Clock is stamped with the time it is written')
	s.put('Sim_Clock = (Sim_Clock + 1) MOD 1000000;')
	s.begin('Sim_LastMs > 0')
	s.put('Sim_Dt = Min(1000, Max(0, Sim_Clock.TimeDate - Sim_LastMs));')
	s.otherwise()
	s.put('Sim_Dt = 0;')
	s.end()
	s.put('Sim_LastMs = Sim_Clock.TimeDate;')

	r01_defaults(s)
	r02_simulation(s)
	r03_input_map(s)
	r04_analogs(s)
	r05_recipe(s)
	r06_bulk_tank_totes(s)
	r07_sequence(s)
	r08_interlocks(s)
	r09_valves(s)
	r10_motors(s)
	r11_alarms(s)
	r12_output_map(s)

	s.section('R13 HMI: reset batch counter')
	s.begin('PB_ResetCount')
	s.put('Bat_Count = 0;')
	s.end()

	s.section('End of pass: clear the momentary pushbuttons')
	for pb in PUSHBUTTONS:
		s.unlatch(pb)

	return s.text()


def load_defaults(s):
	for tag, value in [('Cfg_MaxBatch', 12.0), ('Cfg_Density0', 8.2), ('Cfg_Density1', 8.1), ('Cfg_Density2', 8.3),
			('Cfg_Dribble0', 3.0), ('Cfg_Dribble1', 3.0), ('Cfg_Dribble2', 3.0),
			('Cfg_Preact0', 0.3), ('Cfg_Preact1', 0.3), ('Cfg_Preact2', 0.3), ('Cfg_PreactGain', 0.5),
			('Cfg_PreactMax', 2.0), ('Cfg_Tolerance', 0.5), ('Cfg_MinTarget', 0.1), ('Cfg_SettleTime', 2.0),
			('Cfg_NoFlowTime', 10.0), ('Cfg_NoFlowDelta', 0.2), ('Cfg_EmptyWt', 1.0), ('Cfg_FillMaxPress', 3.0),
			('Cfg_VentedPress', 2.0), ('Cfg_ChargePress', 40.0), ('Cfg_ChargeTimeout', 30.0),
			('Cfg_EmptyPress', 10.0), ('Cfg_EmptyDebounce', 2.0), ('Cfg_DischargeTimeout', 120.0),
			('Cfg_VentCloseDelay', 1.0), ('Cfg_VentTimeout', 30.0), ('Cfg_DayTankLowDelay', 300.0),
			('Cfg_AutoRestartDelay', 2.0), ('Rcp_TotalVol', 10.0), ('Rcp_Pct0', 60.0), ('Rcp_Pct1', 30.0),
			('Rcp_Pct2', 10.0), ('Cfg_PreactAuto', 1), ('Cfg_RecircAutoRun', 1)]:
		s.put('%s = %s;' % (tag, value))
	s.note('Valve travel times: 1 s without limit switches, 5 s with; pump feedback time')
	for v in VALVES:
		s.put('%s_TravelTime = %s;' % (v, '1.0' if v in NO_LIMITS else '5.0'))
	for p in PUMPS:
		s.put('%s_FailTime = 5.0;' % p)
	s.put('Cfg_DefaultsLoaded = 1;')


def sim_starting_values(s):
	for tag, value in [('Sim_Tank1', 8000.0), ('Sim_ToteSize', 275.0), ('Sim_Tote2', 275.0), ('Sim_Tote3', 275.0),
			('Sim_Day2', 30.0), ('Sim_Day3', 30.0), ('Sim_Hopper', 0.0), ('Sim_Press', 0.0),
			('Sim_AirSupply', 60.0), ('Sim_FastRate', 4.0), ('Sim_SlowRate', 0.4), ('Sim_DischRate', 6.0),
			('Sim_RefillRate', 10.0)]:
		s.put('%s = %s;' % (tag, value))


def r01_defaults(s):
	s.section('R01 DEFAULTS: setpoints and recipe, on HMI Load Defaults (the first run\'s are in On start)')
	s.begin('PB_LoadDefaults')
	load_defaults(s)
	s.end()

	s.note('Preact gain 0..1, preacts 0..PreactMax')
	s.put('IF Cfg_PreactGain < 0 THEN Cfg_PreactGain = 0; ENDIF;')
	s.put('IF Cfg_PreactGain > 1 THEN Cfg_PreactGain = 1; ENDIF;')
	for i in range(3):
		s.put('IF Cfg_Preact%d < 0 THEN Cfg_Preact%d = 0; ENDIF;' % (i, i))
		s.put('IF Cfg_Preact%d > Cfg_PreactMax THEN Cfg_Preact%d = Cfg_PreactMax; ENDIF;' % (i, i))

	s.note('Simulation starting values again on RESET SIM VALUES (On start sets them first)')
	s.begin('Sim_PB_Reinit')
	sim_starting_values(s)
	s.put('Sim_PB_Reinit = 0;')
	s.end()


def r02_simulation(s):
	s.section('R02 SIMULATION: the process, in place of physical IO (IO Sim screen, Simulation ON)')
	s.begin('Sim_Enable')
	s.coil('DI_ES_001_EStopped', 'Sim_EStopPB')
	s.note('Refill valve SV-102 coil is hardwired through CR1: it drops out on tank high-high')
	s.coil('Sim_CoilSV102', 'DO_SV_102 AND NOT DI_LSH_100')
	s.note('Valve travel: 1.5 s to open, 0.5 s to close; liquid flows once a valve leaves its closed seat')
	for n, coil in SIM_VALVES:
		s.ton('Sim_OpenAcc_' + n, 'Sim_OpenDn_' + n, coil, 1500)
		s.tof('Sim_CloseAcc_' + n, 'Sim_CloseDn_' + n, coil, 500)
		s.coil('DI_ZSO_' + n, 'Sim_OpenDn_' + n)
		s.coil('DI_ZSC_' + n, 'NOT Sim_CloseDn_' + n)
	s.note('Pump run feedback follows the run command')
	for n in ('100', '200', '300'):
		s.coil('DI_MI_' + n, 'DO_MS_' + n)

	s.note('Flows into the hopper (lb/s) while the pump runs and its source is not empty')
	supply = {1: 'DI_MI_100 AND Sim_Tank1 > 100', 2: 'DI_MI_200 AND Sim_Day2 > 0.5', 3: 'DI_MI_300 AND Sim_Day3 > 0.5'}
	for i, (fast, slow) in {1: ('101', '100'), 2: ('201', '200'), 3: ('301', '300')}.items():
		s.set('Sim_Flow%d' % i, '(%s AND NOT DI_ZSC_%s) * Sim_FastRate + (%s AND NOT DI_ZSC_%s) * Sim_SlowRate'
			% (supply[i], fast, supply[i], slow))
	s.coil('Sim_DischOpen', 'NOT DI_ZSC_401 OR NOT DI_ZSC_402')
	s.note('Discharge flow to the mixer needs air pressure in the hopper')
	s.set('Sim_DischFlow', '((NOT DI_ZSC_401 AND Sim_Press > 3) + (NOT DI_ZSC_402 AND Sim_Press > 3)) * Sim_DischRate')

	s.note('Integrate hopper weight and source levels')
	s.set('Sim_Hopper', 'Sim_Hopper + (Sim_Flow1 + Sim_Flow2 + Sim_Flow3 - Sim_DischFlow) * Sim_Dt / 1000')
	s.set('Sim_Tank1', 'Sim_Tank1 - Sim_Flow1 * Sim_Dt / 1000 / Cfg_Density0')
	s.set('Sim_Day2', 'Sim_Day2 - Sim_Flow2 * Sim_Dt / 1000 / Cfg_Density1')
	s.set('Sim_Day3', 'Sim_Day3 - Sim_Flow3 * Sim_Dt / 1000 / Cfg_Density2')
	s.note('Bulk tank refill (rate accelerated for demonstration)')
	s.put('IF NOT DI_ZSC_102 THEN Sim_Tank1 = Sim_Tank1 + Sim_RefillRate * Sim_Dt / 1000; ENDIF;')
	for n in (2, 3):
		s.note('Tote #%d gravity feeds its day tank (2 gal/s) while the tote has liquid' % n)
		s.begin('Sim_Day%d < 30 AND Sim_Tote%d > 0' % (n, n))
		s.put('Sim_Day%d = Sim_Day%d + 2 * Sim_Dt / 1000;' % (n, n))
		s.put('Sim_Tote%d = Sim_Tote%d - 2 * Sim_Dt / 1000;' % (n, n))
		s.end()
		s.begin('Sim_PB_ReplaceTote%d' % n)
		s.put('Sim_Tote%d = Sim_ToteSize;' % n)
		s.put('Sim_PB_ReplaceTote%d = 0;' % n)
		s.end()
	s.note('Limits')
	for tag in ('Sim_Hopper', 'Sim_Tote2', 'Sim_Tote3', 'Sim_Tank1'):
		s.put('IF %s < 0 THEN %s = 0; ENDIF;' % (tag, tag))
	s.put('IF Sim_Tank1 > 13500 THEN Sim_Tank1 = 13500; ENDIF;')

	s.note('Hopper pressure: held when sealed, bled down when empty and open, charged by air, blown through when expelled, vented')
	s.begin('DO_SV_001')
	s.set('Sim_PressTarget', '0')
	s.otherwise()
	s.begin('DO_SV_002 AND NOT Sim_AirSupplyFail')
	s.begin('Sim_DischOpen AND Sim_Hopper <= 0.05')
	s.set('Sim_PressTarget', 'Sim_BlowThru')
	s.otherwise()
	s.set('Sim_PressTarget', 'Sim_AirSupply')
	s.end()
	s.otherwise()
	s.begin('Sim_DischOpen AND Sim_Hopper <= 0.05')
	s.set('Sim_PressTarget', '0')
	s.otherwise()
	s.set('Sim_PressTarget', 'Sim_Press')
	s.end()
	s.end()
	s.end()
	s.note('Approach the target by 35 % every 100 ms')
	s.begin('Abs(Sim_PressTarget - Sim_Press) < 0.01')
	s.set('Sim_Press', 'Sim_PressTarget')
	s.otherwise()
	s.put('Sim_Press = Sim_Press + (Sim_PressTarget - Sim_Press) * Min(1, 0.35 * Sim_Dt / 100);')
	s.end()

	s.note('Transmitter signals, 4-20 mA')
	s.set('LIT100_mA', '4 + 16 * Sim_Tank1 / 13500')
	s.set('PT001_mA', '4 + 16 * Sim_Press / 100')
	s.set('WT001_mA', '4 + 16 * Sim_Hopper / 150')
	s.note('Bulk tank floats: low 10 %, high 85 %, high-high 92 %; day tank lows 10 gal; hopper high 125 lb')
	s.coil('DI_LSL_100', 'Sim_Tank1 < 1350')
	s.coil('DI_LSA_100', 'Sim_Tank1 > 11475')
	s.coil('DI_LSH_100', 'Sim_Tank1 > 12420')
	s.coil('DI_LSL_200', 'Sim_Day2 < 10')
	s.coil('DI_LSL_300', 'Sim_Day3 < 10')
	s.coil('DI_LSH_001', 'Sim_Hopper > 125')
	s.end()


def r03_input_map(s):
	s.section('R03 INPUT MAP: limit switches and run feedback')
	for v in VALVES:
		if v not in NO_LIMITS:
			s.coil(v + '_ZSO', 'DI_ZSO_' + v[2:])
			s.coil(v + '_ZSC', 'DI_ZSC_' + v[2:])


def r04_analogs(s):
	s.section('R04 ANALOGS: bad signal below 3.6 mA or above 21 mA for 1 s; scaling')
	for tag, ma, top in (('LIT100', 'LIT100_mA', 100), ('PT001', 'PT001_mA', 100), ('WT001', 'WT001_mA', 150)):
		s.ton(tag + '_BadAcc', 'Alm_%s_Bad' % tag, '%s < 3.6 OR %s > 21' % (ma, ma), 1000)
		s.set(tag, '(%s - 4) * %s / 16' % (ma, top))
	s.set('LIT100_Gal', 'LIT100 * 135')


def r05_recipe(s):
	s.section('R05 RECIPE: targets in lb = total gal x % x lb/gal; validity')
	s.set('Rcp_PctSum', 'Rcp_Pct0 + Rcp_Pct1 + Rcp_Pct2')
	for i in range(3):
		s.set('Rcp_Target%d' % i, 'Rcp_TotalVol * Rcp_Pct%d * 0.01 * Cfg_Density%d' % (i, i))
	s.set('Rcp_TotalTarget', 'Rcp_Target0 + Rcp_Target1 + Rcp_Target2')
	s.coil('Rcp_Err_PctSum', 'Abs(Rcp_PctSum - 100) > 0.1')
	s.coil('Rcp_Err_Volume', 'Rcp_TotalVol <= 0 OR Rcp_TotalVol > Cfg_MaxBatch')
	s.coil('Rcp_Err_NegPct', 'Rcp_Pct0 < 0 OR Rcp_Pct1 < 0 OR Rcp_Pct2 < 0')
	s.coil('Rcp_Err_OverScale', 'Rcp_TotalTarget > 150 * 0.9')
	s.coil('Rcp_Valid', 'NOT Rcp_Err_PctSum AND NOT Rcp_Err_Volume AND NOT Rcp_Err_NegPct AND NOT Rcp_Err_OverScale')


def r06_bulk_tank_totes(s):
	s.section('R06 BULK TANK AND TOTES: refill valve SV-102 is operator controlled only')
	s.latch('SV102_Manual')
	s.latch('SV102_CmdOpen', 'DI_HSO_100 OR PB_RefillOpen')
	s.latch('SV102_CmdClose', 'DI_HSC_100 OR PB_RefillClose')
	s.note('Day tank low: alarm at once; pump inhibited after Cfg_DayTankLowDelay so the reserve is not run dry')
	s.ton('DT2_LowAcc', 'DT2_LowDn', 'DI_LSL_200', 'Cfg_DayTankLowDelay * 1000')
	s.ton('DT3_LowAcc', 'DT3_LowDn', 'DI_LSL_300', 'Cfg_DayTankLowDelay * 1000')


def r07_sequence(s):
	s.section('R07 SEQUENCE. Steps: 0 idle, 5 tare, 10-14 / 20-24 / 30-34 plasticizers 1-3 (setup, fast, dribble, '
		'settle, tolerance hold), 40 fill complete, 50 close vent, 55 pressurize, 60 charged, 70 discharging, '
		'80 venting, 90 complete, 100 held, 110 aborting')
	s.note('StepInit marks the first pass in a new step; transitions write Seq_NextStep, committed at the end')
	s.coil('Seq_StepInit', 'Seq_Step <> Seq_PrevStep')
	s.set('Seq_PrevStep', 'Seq_Step')
	s.set('Seq_NextStep', 'Seq_Step')
	s.begin('Seq_Step == 100')
	s.set('Seq_ActStep', 'Seq_HoldStep')
	s.otherwise()
	s.set('Seq_ActStep', 'Seq_Step')
	s.end()
	s.coil('Seq_Idle', 'Seq_Step == 0')
	s.coil('Seq_Running', 'Seq_Step >= 5 AND Seq_Step <= 90')
	s.coil('Seq_Held', 'Seq_Step == 100')
	s.coil('Seq_Aborting', 'Seq_Step == 110')
	s.note('Time in step and batch cycle time (ms)')
	s.begin('NOT Seq_StepInit')
	s.put('Seq_StepTime = Seq_StepTime + Sim_Dt;')
	s.otherwise()
	s.set('Seq_StepTime', '0')
	s.end()
	s.begin('Seq_Step >= 5 AND Seq_Step <= 100')
	s.put('Seq_CycleTime = Seq_CycleTime + Sim_Dt;')
	s.otherwise()
	s.set('Seq_CycleTime', '0')
	s.end()

	s.note('Step entry actions')
	s.begin('Seq_Step == 0')
	s.latch('Seq_VentOn')
	s.unlatch('Seq_PressOn')
	s.end()
	s.begin('Seq_StepInit')
	s.begin('Seq_Step == 5')
	s.note('Tare: freeze this batch')
	for i in range(3):
		s.put('Bat_Target%d = Rcp_Target%d;' % (i, i))
		s.put('Bat_Actual%d = 0;' % i)
	s.put('Bat_Volume = Rcp_TotalVol;')
	s.put('Bat_Total = 0;')
	s.end()
	for i in range(3):
		s.begin('Seq_Step == %d' % (10 * (i + 1)))
		s.note('Plast #%d setup: target, dribble and preact cutoffs, starting weight' % (i + 1))
		s.put('Seq_IngIdx = %d;' % i)
		s.put('Seq_IngTarget = Bat_Target%d;' % i)
		s.put('Seq_IngDribble = Cfg_Dribble%d;' % i)
		s.put('Seq_IngPreact = Cfg_Preact%d;' % i)
		s.put('Seq_IngFastCutoff = Seq_IngTarget - Seq_IngDribble;')
		s.put('Seq_IngFinalCutoff = Seq_IngTarget - Seq_IngPreact;')
		s.put('Seq_IngStartWt = Seq_NetWt;')
		s.put('Seq_IngActual = 0;')
		s.put('Seq_NoFlowRef = 0;')
		s.end()
	s.unlatch('Seq_VentOn', 'Seq_Step == 50')
	s.latch('Seq_PressOn', 'Seq_Step == 55')
	s.begin('Seq_Step == 80')
	s.unlatch('Seq_PressOn')
	s.latch('Seq_VentOn')
	s.end()
	s.begin('Seq_Step == 90')
	s.note('Complete: count the batch, record cycle time, tell the mixer; check the hopper is empty')
	s.begin('NOT Seq_EmptyOnly')
	s.put('Bat_Count = Bat_Count + 1;')
	s.put('Bat_LastCycle = Seq_CycleTime / 1000;')
	s.put('Mix_PlastDelivered = 1;')
	s.end()
	s.latch('Alm_Residual', 'WT001 > Cfg_EmptyWt')
	s.end()
	s.begin('Seq_Step == 110')
	s.unlatch('Seq_PressOn')
	s.latch('Seq_VentOn')
	s.unlatch('Seq_EmptyOnly')
	s.end()
	s.end()

	s.note('Net weight for this batch, and weight added for the current ingredient')
	s.set('Seq_NetWt', 'WT001 - Seq_TareWt')
	s.begin('Seq_ActStep >= 10 AND Seq_ActStep <= 34')
	s.set('Seq_IngActual', 'Seq_NetWt - Seq_IngStartWt')
	for i in range(3):
		s.put('IF Seq_IngIdx == %d AND Bat_Actual%d <> Seq_IngActual THEN Bat_Actual%d = Seq_IngActual; ENDIF;' % (i, i, i))
	s.end()

	s.note('Not-ready reasons')
	s.coil('Seq_NR_EStop', 'DI_ES_001_EStopped')
	s.coil('Seq_NR_DevManual', any_of([d + '_Manual' for d in SEQ_DEVICES]))
	s.coil('Seq_NR_DevFault', any_of([d + '_Fault' for d in SEQ_DEVICES]))
	s.coil('Seq_NR_Analog', 'Alm_WT001_Bad OR Alm_PT001_Bad')
	s.coil('Seq_NR_Recipe', 'NOT Rcp_Valid')
	s.coil('Seq_NR_HopperNotEmpty', 'WT001 > Cfg_EmptyWt')
	s.coil('Seq_NR_NotVented', 'PT001 > Cfg_VentedPress OR NOT SV001_Opened')
	s.coil('Seq_NR_HopperHigh', 'DI_LSH_001')
	s.coil('Seq_NR_Plast1', 'Rcp_Target0 > Cfg_MinTarget AND (NOT P100_Running OR DI_LSL_100)')
	s.coil('Seq_NR_Plast2', 'Rcp_Target1 > Cfg_MinTarget AND DT2_LowDn')
	s.coil('Seq_NR_Plast3', 'Rcp_Target2 > Cfg_MinTarget AND DT3_LowDn')
	reasons = ['EStop', 'DevManual', 'DevFault', 'Analog', 'Recipe', 'HopperNotEmpty', 'NotVented', 'HopperHigh',
		'Plast1', 'Plast2', 'Plast3']
	s.coil('Seq_StartPerm', ' AND '.join('NOT Seq_NR_' + r for r in reasons))
	s.coil('Seq_EmptyPerm', ' AND '.join('NOT Seq_NR_' + r for r in reasons[:4]))

	s.note('Supply lost while adding an ingredient')
	s.coil('Alm_Plast1_Unavail', 'Seq_ActStep >= 11 AND Seq_ActStep <= 12 AND (NOT P100_Running OR DI_LSL_100)')
	s.coil('Alm_Plast2_Unavail', 'Seq_ActStep >= 21 AND Seq_ActStep <= 22 AND DT2_LowDn')
	s.coil('Alm_Plast3_Unavail', 'Seq_ActStep >= 31 AND Seq_ActStep <= 32 AND DT3_LowDn')
	s.note('Hold causes: device in Manual or faulted, bad transmitter, hopper high while filling, supply lost')
	s.coil('Seq_HoldCause', 'Seq_ActStep >= 5 AND Seq_ActStep <= 90 AND (Seq_NR_DevManual OR Seq_NR_DevFault OR '
		'Seq_NR_Analog OR (Seq_ActStep >= 10 AND Seq_ActStep <= 34 AND DI_LSH_001) OR Alm_Plast1_Unavail OR '
		'Alm_Plast2_Unavail OR Alm_Plast3_Unavail)')
	s.note('No-flow monitor: weight must rise by Cfg_NoFlowDelta every Cfg_NoFlowTime while a fill valve is open')
	s.coil('Seq_FillActive', '(Seq_Step == 11 AND NOT SV101_Closed) OR (Seq_Step == 21 AND NOT SV201_Closed) OR '
		'(Seq_Step == 31 AND NOT SV301_Closed) OR (Seq_Step == 12 AND NOT SV100_Closed) OR '
		'(Seq_Step == 22 AND NOT SV200_Closed) OR (Seq_Step == 32 AND NOT SV300_Closed)')
	s.begin('NOT Seq_FillActive OR Seq_StepInit OR Seq_IngActual > Seq_NoFlowRef + Cfg_NoFlowDelta')
	s.set('Seq_NoFlowRef', 'Seq_IngActual')
	s.put('IF Seq_NoFlowAcc <> 0 THEN Seq_NoFlowAcc = 0; ENDIF;')
	s.unlatch('Seq_NoFlowDn')
	s.end()
	s.ton('Seq_NoFlowAcc', 'Seq_NoFlowDn', 'Seq_FillActive', 'Cfg_NoFlowTime * 1000')

	s.note('TRANSITIONS')
	s.note('Idle to Tare: Start (or auto-cycle) with all start permissives')
	s.put('IF Seq_Step == 0 AND (PB_Start OR (Sel_AutoCycle AND Seq_StepTime >= Cfg_AutoRestartDelay * 1000)) '
		'AND Seq_StartPerm THEN Seq_NextStep = 5; ENDIF;')
	s.note('Idle to Close vent: Empty Hopper')
	s.begin('Seq_Step == 0 AND PB_EmptyHopper AND Seq_EmptyPerm')
	s.put('Seq_EmptyOnly = 1;')
	s.put('Seq_NextStep = 50;')
	s.end()
	s.note('Tare to Plast #1: scale settled, capture tare')
	s.begin('Seq_Step == 5 AND NOT Seq_StepInit AND Seq_StepTime >= Cfg_SettleTime * 1000')
	s.put('Seq_TareWt = WT001;')
	s.put('Seq_NextStep = 10;')
	s.end()
	for i in range(3):
		b = 10 * (i + 1)
		nxt = b + 10
		s.note('PLAST #%d: skip if not in the recipe; fast fill unless the target is under the dribble weight' % (i + 1))
		s.begin('Seq_Step == %d AND NOT Seq_StepInit' % b)
		s.put('IF Seq_IngTarget < Cfg_MinTarget THEN Seq_NextStep = %d; ENDIF;' % nxt)
		s.put('IF Seq_IngTarget >= Cfg_MinTarget AND Seq_IngFastCutoff > 0 THEN Seq_NextStep = %d; ENDIF;' % (b + 1))
		s.put('IF Seq_IngTarget >= Cfg_MinTarget AND Seq_IngFastCutoff <= 0 THEN Seq_NextStep = %d; ENDIF;' % (b + 2))
		s.end()
		s.put('IF Seq_Step == %d AND NOT Seq_StepInit AND Seq_IngActual >= Seq_IngFastCutoff THEN Seq_NextStep = %d; ENDIF;'
			% (b + 1, b + 2))
		s.put('IF Seq_Step == %d AND NOT Seq_StepInit AND Seq_IngActual >= Seq_IngFinalCutoff THEN Seq_NextStep = %d; ENDIF;'
			% (b + 2, b + 3))
		s.note('Settle: adapt the preact by the error, check tolerance')
		s.begin('Seq_Step == %d AND NOT Seq_StepInit AND Seq_StepTime >= Cfg_SettleTime * 1000' % (b + 3))
		s.put('IF Cfg_PreactAuto THEN Cfg_Preact%d = Cfg_Preact%d + (Seq_IngActual - Seq_IngTarget) * Cfg_PreactGain; ENDIF;'
			% (i, i))
		s.put('IF Abs(Seq_IngActual - Seq_IngTarget) <= Cfg_Tolerance THEN Seq_NextStep = %d; ENDIF;' % nxt)
		s.begin('Abs(Seq_IngActual - Seq_IngTarget) > Cfg_Tolerance')
		s.put('Alm_OutOfTol = 1;')
		s.put('Seq_NextStep = %d;' % (b + 4))
		s.end()
		s.end()
		s.note('Out of tolerance: the operator accepts, or tops off when under weight')
		s.begin('Seq_Step == %d AND PB_AcceptTol' % (b + 4))
		s.put('Alm_OutOfTol = 0;')
		s.put('Seq_NextStep = %d;' % nxt)
		s.end()
		s.begin('Seq_Step == %d AND PB_TopOff AND Seq_IngActual < Seq_IngTarget' % (b + 4))
		s.put('Alm_OutOfTol = 0;')
		s.put('Seq_IngFinalCutoff = Seq_IngTarget - Cfg_Tolerance * 0.5;')
		s.put('Seq_NextStep = %d;' % (b + 2))
		s.end()
	s.note('Fill complete: record the total')
	s.begin('Seq_Step == 40 AND NOT Seq_StepInit')
	s.put('Bat_Total = Seq_NetWt;')
	s.put('Seq_NextStep = 50;')
	s.end()
	s.put('IF Seq_Step == 50 AND NOT Seq_StepInit AND SV001_Closed AND Seq_StepTime >= Cfg_VentCloseDelay * 1000 '
		'THEN Seq_NextStep = 55; ENDIF;')
	s.note('Pressurize: timeout holds; charged at charge pressure')
	s.begin('Seq_Step == 55 AND NOT Seq_StepInit AND Seq_StepTime >= Cfg_ChargeTimeout * 1000')
	s.put('Alm_ChargeTimeout = 1;')
	s.put('Seq_HoldStep = 55;')
	s.put('Seq_NextStep = 100;')
	s.end()
	s.put('IF Seq_Step == 55 AND NOT Seq_StepInit AND PT001 >= Cfg_ChargePress THEN Seq_NextStep = 60; ENDIF;')
	s.note('Charged: discharge when the mixer calls for plasticizer, on Discharge, or when only emptying')
	s.put('IF Seq_Step == 60 AND (PB_Discharge OR (Mix_CallForPlast AND NOT Mix_PlastDelivered) OR Seq_EmptyOnly) '
		'THEN Seq_NextStep = 70; ENDIF;')
	s.note('Discharging: when the liquid is gone, air blows through and the hopper pressure drops')
	s.ton('Seq_EmptyAcc', 'Seq_EmptyDn', 'Seq_Step == 70 AND SV401_Opened AND SV402_Opened AND PT001 < Cfg_EmptyPress',
		'Cfg_EmptyDebounce * 1000')
	s.begin('Seq_Step == 70 AND NOT Seq_StepInit AND Seq_StepTime >= Cfg_DischargeTimeout * 1000')
	s.put('Alm_DischTimeout = 1;')
	s.put('Seq_HoldStep = 70;')
	s.put('Seq_NextStep = 100;')
	s.end()
	s.put('IF Seq_Step == 70 AND NOT Seq_StepInit AND Seq_EmptyDn THEN Seq_NextStep = 80; ENDIF;')
	s.note('Venting')
	s.begin('Seq_Step == 80 AND NOT Seq_StepInit AND Seq_StepTime >= Cfg_VentTimeout * 1000')
	s.put('Alm_VentTimeout = 1;')
	s.put('Seq_HoldStep = 80;')
	s.put('Seq_NextStep = 100;')
	s.end()
	s.put('IF Seq_Step == 80 AND NOT Seq_StepInit AND SV401_Closed AND SV402_Closed AND SV002_Closed AND SV001_Opened '
		'AND PT001 < Cfg_VentedPress THEN Seq_NextStep = 90; ENDIF;')
	s.begin('Seq_Step == 90 AND NOT Seq_StepInit')
	s.unlatch('Seq_EmptyOnly')
	s.put('Seq_NextStep = 0;')
	s.end()
	s.note('Hold: Hold pushbutton, a hold cause or no flow; resume once the causes are cleared')
	s.latch('Alm_NoFlow', 'Seq_Step >= 5 AND Seq_Step <= 90 AND Seq_NoFlowDn')
	s.begin('Seq_Step >= 5 AND Seq_Step <= 90 AND (PB_Hold OR Seq_HoldCause OR Seq_NoFlowDn)')
	s.put('Seq_HoldStep = Seq_Step;')
	s.put('Seq_NextStep = 100;')
	s.end()
	s.put('IF Seq_Step == 100 AND PB_Resume AND NOT Seq_HoldCause THEN Seq_NextStep = Seq_HoldStep; ENDIF;')
	s.note('Abort: Abort pushbutton or E-stop; back to idle once vented')
	s.put('IF (PB_Abort OR DI_ES_001_EStopped) AND Seq_Step >= 5 AND Seq_Step <= 100 THEN Seq_NextStep = 110; ENDIF;')
	s.put('IF Seq_Step == 110 AND NOT Seq_StepInit AND NOT DI_ES_001_EStopped AND SV001_Opened AND '
		'(PT001 < Cfg_VentedPress OR (Alm_PT001_Bad AND Seq_StepTime >= Cfg_VentTimeout * 1000)) '
		'THEN Seq_NextStep = 0; ENDIF;')
	s.note('Commit the step change')
	s.set('Seq_Step', 'Seq_NextStep')

	s.note('Device requests from the sequence')
	s.coil('SV001_AutoCmd', 'Seq_VentOn')
	s.coil('SV002_AutoCmd', 'Seq_PressOn')
	s.coil('P100_AutoCmd', 'Cfg_RecircAutoRun')
	for i, (pump, fast, slow) in enumerate((('P100', 'SV101', 'SV100'), ('P200', 'SV201', 'SV200'),
			('P300', 'SV301', 'SV300'))):
		b = 10 * (i + 1)
		if pump != 'P100':
			s.coil(pump + '_AutoCmd', 'Seq_Step >= %d AND Seq_Step <= %d' % (b + 1, b + 2))
		s.coil(fast + '_AutoCmd', 'Seq_Step == %d AND %s_Running' % (b + 1, pump))
		s.coil(slow + '_AutoCmd', 'Seq_Step == %d AND %s_Running' % (b + 2, pump))
	s.coil('SV401_AutoCmd', 'Seq_Step == 70')
	s.coil('SV402_AutoCmd', 'Seq_Step == 70')
	s.note('Mixer handshake: PlastDelivered stays on until the mixer drops CallForPlast')
	s.coil('Mix_PlastReady', 'Seq_Step == 60')
	s.coil('Mix_Discharging', 'Seq_Step == 70')
	s.unlatch('Mix_PlastDelivered', 'NOT Mix_CallForPlast AND Seq_Step <> 90')


def r08_interlocks(s):
	s.section('R08 INTERLOCKS')
	for pb, cmd in (('PB_AllAuto', 'CmdAuto'), ('PB_AllManual', 'CmdManual')):
		s.begin(pb)
		for d in SEQ_DEVICES:
			s.put('%s_%s = 1;' % (d, cmd))
		s.end()
	s.coil('Seq_FillValvesClosed', ' AND '.join(v + '_Closed' for v in ('SV100', 'SV101', 'SV200', 'SV201', 'SV300', 'SV301')))
	s.note('Fill valves: no E-stop, hopper not high, not pressurized, air and discharge valves closed')
	s.coil('Seq_FillPerm', 'NOT DI_ES_001_EStopped AND NOT DI_LSH_001 AND NOT Alm_PT001_Bad AND '
		'PT001 < Cfg_FillMaxPress AND NOT SV002_Cmd AND SV401_Closed AND SV402_Closed')
	s.coil('SV100_Perm', 'Seq_FillPerm AND NOT DI_LSL_100')
	s.coil('SV101_Perm', 'Seq_FillPerm AND NOT DI_LSL_100')
	for v in ('SV200', 'SV201', 'SV300', 'SV301'):
		s.coil(v + '_Perm', 'Seq_FillPerm')
	s.coil('SV001_Perm', 'NOT DI_ES_001_EStopped')
	s.note('Pressurize: vent closed and fill valves closed (no air back into the supply lines)')
	s.coil('SV002_Perm', 'NOT DI_ES_001_EStopped AND SV001_Closed AND Seq_FillValvesClosed')
	s.coil('SV401_Perm', 'NOT DI_ES_001_EStopped AND Seq_FillValvesClosed')
	s.coil('SV402_Perm', 'NOT DI_ES_001_EStopped AND Seq_FillValvesClosed')
	s.coil('SV102_Perm', 'NOT DI_ES_001_EStopped AND NOT DI_LSH_100')
	s.coil('P100_Perm', 'NOT DI_ES_001_EStopped AND NOT DI_LSL_100')
	s.coil('P200_Perm', 'NOT DI_ES_001_EStopped AND NOT DT2_LowDn')
	s.coil('P300_Perm', 'NOT DI_ES_001_EStopped AND NOT DT3_LowDn')


def mode_logic(s, d, on, off):
	"""Manual/Auto and the operator's on/off commands, shared by valves and pumps."""
	s.begin('%s_CmdManual AND NOT %s_Manual' % (d, d))
	s.put('IF %s_Cmd THEN %s_ManCmd = 1; ENDIF;' % (d, d))
	s.put('%s_Manual = 1;' % d)
	s.end()
	if d != 'SV102':
		s.begin('%s_CmdAuto' % d)
		s.unlatch(d + '_Manual')
		s.unlatch(d + '_ManCmd')
		s.end()
	s.latch(d + '_ManCmd', '%s_Manual AND %s_%s AND %s_Perm' % (d, d, on, d))
	s.unlatch(d + '_ManCmd', '(%s_Manual AND %s_%s) OR NOT %s_Perm' % (d, d, off, d))


def clear_commands(s, d, names):
	for n in names:
		s.unlatch('%s_%s' % (d, n))


def r09_valves(s):
	s.section('R09 VALVES: Auto/Manual, permissives, position and travel faults')
	for v in VALVES:
		s.note(v)
		mode_logic(s, v, 'CmdOpen', 'CmdClose')
		s.coil(v + '_Cmd', '((%s_Manual AND %s_ManCmd) OR (NOT %s_Manual AND %s_AutoCmd)) AND %s_Perm AND NOT %s_FltOpen'
			% (v, v, v, v, v, v))
		moving = '(%s_Cmd AND NOT %s_Opened) OR (NOT %s_Cmd AND NOT %s_Closed)' % (v, v, v, v)
		if v in NO_LIMITS:
			s.note('No limit switches: position assumed after the travel time')
			s.unlatch(v + '_Closed', v + '_Cmd')
			s.unlatch(v + '_Opened', 'NOT %s_Cmd' % v)
			s.ton(v + '_TravelAcc', v + '_TravelDn', moving, v + '_TravelTime * 1000')
			s.latch(v + '_Opened', '%s_TravelDn AND %s_Cmd' % (v, v))
			s.latch(v + '_Closed', '%s_TravelDn AND NOT %s_Cmd' % (v, v))
		else:
			s.coil(v + '_Opened', '%s_ZSO AND NOT %s_ZSC' % (v, v))
			s.coil(v + '_Closed', '%s_ZSC AND NOT %s_ZSO' % (v, v))
			s.ton(v + '_TravelAcc', v + '_TravelDn', moving, v + '_TravelTime * 1000')
			s.latch(v + '_FltOpen', '%s_TravelDn AND %s_Cmd' % (v, v))
			s.latch(v + '_FltClose', '%s_TravelDn AND NOT %s_Cmd' % (v, v))
		s.begin('PB_Reset')
		s.unlatch(v + '_FltOpen')
		s.unlatch(v + '_FltClose')
		s.end()
		s.coil(v + '_Fault', '%s_FltOpen OR %s_FltClose' % (v, v))
		status(s, v, [('%s_Fault' % v, 3), ('%s_Opened' % v, 1), ('%s_Closed' % v, 0)], 2)
		clear_commands(s, v, ('CmdOpen', 'CmdClose', 'CmdAuto', 'CmdManual'))


def status(s, d, cases, default):
	"""First matching case wins (the PLC's last MOV)."""
	for cond, value in cases:
		s.begin(cond)
		s.set(d + '_Status', str(value))
		s.otherwise()
	s.set(d + '_Status', str(default))
	for _ in cases:
		s.end()


def r10_motors(s):
	s.section('R10 MOTORS: Auto/Manual, permissives, run feedback faults, H-O-A in Hand')
	for p in PUMPS:
		fbk = 'DI_MI_' + p[1:]
		s.note(p)
		mode_logic(s, p, 'CmdStart', 'CmdStop')
		s.coil(p + '_Cmd', '((%s_Manual AND %s_ManCmd) OR (NOT %s_Manual AND %s_AutoCmd)) AND %s_Perm AND NOT %s_Fault'
			% (p, p, p, p, p, p))
		s.coil(p + '_Running', fbk)
		s.note('Command and feedback disagree: fail to start, or running in Hand')
		s.ton(p + '_FbkAcc', p + '_FbkDn', '(%s_Cmd AND NOT %s) OR (NOT %s_Cmd AND %s)' % (p, fbk, p, fbk),
			p + '_FailTime * 1000')
		s.latch(p + '_FltFailStart', '%s_FbkDn AND %s_Cmd' % (p, p))
		s.coil(p + '_HandRun', '%s_FbkDn AND NOT %s_Cmd AND %s' % (p, p, fbk))
		s.unlatch(p + '_FltFailStart', 'PB_Reset')
		s.coil(p + '_Fault', p + '_FltFailStart')
		status(s, p, [('%s_Fault' % p, 3), ('%s_HandRun' % p, 4), ('%s_Running' % p, 1),
			('%s_Cmd AND NOT %s_Running' % (p, p), 2)], 0)
		clear_commands(s, p, ('CmdStart', 'CmdStop', 'CmdAuto', 'CmdManual'))


def r11_alarms(s):
	s.section('R11 ALARMS')
	s.note('1 Hz flasher')
	s.put('Alm_FlashAcc = Alm_FlashAcc + Sim_Dt;')
	s.put('IF Alm_FlashAcc >= 1000 THEN Alm_FlashAcc = Alm_FlashAcc MOD 1000; ENDIF;')
	s.coil('Alm_Flash', 'Alm_FlashAcc < 500')
	for alm, cond in (('Alm_EStop', 'DI_ES_001_EStopped'), ('Alm_Tank1_High', 'DI_LSA_100'),
			('Alm_Tank1_HiHi', 'DI_LSH_100'), ('Alm_Tank1_Low', 'DI_LSL_100'), ('Alm_DayTank2_Low', 'DI_LSL_200'),
			('Alm_DayTank3_Low', 'DI_LSL_300'), ('Alm_Hopper_High', 'DI_LSH_001')):
		s.coil(alm, cond)
	s.coil('Alm_ValveFault', any_of([v + '_Fault' for v in VALVES]))
	s.coil('Alm_PumpFault', any_of([p + '_Fault' for p in PUMPS]))
	s.note('Reset latched alarms; out of tolerance is cleared by Accept or Top Off')
	s.begin('PB_Reset')
	for alm in ('Alm_NoFlow', 'Alm_ChargeTimeout', 'Alm_DischTimeout', 'Alm_VentTimeout', 'Alm_Residual'):
		s.unlatch(alm)
	s.end()
	s.note('Refill station horn and light: high level sounds, high-high sounds again, until silenced')
	s.latch('Alm_Tank1_Unsilenced', 'Alm_Tank1_High AND NOT Alm_OS0')
	s.coil('Alm_OS0', 'Alm_Tank1_High')
	s.latch('Alm_Tank1_Unsilenced', 'Alm_Tank1_HiHi AND NOT Alm_OS1')
	s.coil('Alm_OS1', 'Alm_Tank1_HiHi')
	s.unlatch('Alm_Tank1_Unsilenced', 'DI_HSS_100 OR PB_Silence OR (NOT Alm_Tank1_High AND NOT Alm_Tank1_HiHi)')
	for n, os in ((2, 2), (3, 3)):
		s.latch('Alm_DayTank%d_Unsilenced' % n, 'Alm_DayTank%d_Low AND NOT Alm_OS%d' % (n, os))
		s.coil('Alm_OS%d' % os, 'Alm_DayTank%d_Low' % n)
		s.unlatch('Alm_DayTank%d_Unsilenced' % n, 'PB_Silence OR NOT Alm_DayTank%d_Low' % n)
	s.coil('Alm_Any', any_of(['Alm_EStop', 'Alm_Tank1_High', 'Alm_Tank1_HiHi', 'Alm_Tank1_Low', 'Alm_DayTank2_Low',
		'Alm_DayTank3_Low', 'Alm_Hopper_High', 'Alm_LIT100_Bad', 'Alm_PT001_Bad', 'Alm_WT001_Bad', 'Alm_ValveFault',
		'Alm_PumpFault', 'Alm_Plast1_Unavail', 'Alm_Plast2_Unavail', 'Alm_Plast3_Unavail', 'Alm_NoFlow',
		'Alm_OutOfTol', 'Alm_ChargeTimeout', 'Alm_DischTimeout', 'Alm_VentTimeout', 'Alm_Residual']))


def r12_output_map(s):
	s.section('R12 OUTPUT MAP: outputs drop on E-stop')
	for p in PUMPS:
		s.coil('DO_MS_' + p[1:], '%s_Cmd AND NOT DI_ES_001_EStopped' % p)
	for v in VALVES:
		s.coil('DO_SV_' + v[2:], '%s_Cmd AND NOT DI_ES_001_EStopped' % v)
	s.note('Lights flash until silenced, then stay on while the condition lasts')
	s.coil('DO_LAH_101', '(Alm_Tank1_High OR Alm_Tank1_HiHi) AND ((Alm_Tank1_Unsilenced AND Alm_Flash) OR NOT Alm_Tank1_Unsilenced)')
	s.coil('DO_LAH_102', 'Alm_Tank1_Unsilenced')
	for n in (2, 3):
		s.coil('DO_LAL_%d00' % n, 'Alm_DayTank%d_Low AND ((Alm_DayTank%d_Unsilenced AND Alm_Flash) OR NOT Alm_DayTank%d_Unsilenced)'
			% (n, n, n))
	s.note('Refill station lamps: steady at position, flashing while travelling')
	s.coil('DO_PLO_100', 'SV102_Opened OR (SV102_Cmd AND NOT SV102_Opened AND Alm_Flash)')
	s.coil('DO_PLC_100', 'SV102_Closed OR (NOT SV102_Cmd AND NOT SV102_Closed AND Alm_Flash)')


# ------------------------------------------------------------------ tags

def new_tags():
	"""Memory tags for what the HMI never showed: timers, internal bits, simulation states."""
	t = []

	def add(name, kind, comment, initial=0):
		t.append({'name': name, 'type': 'Memory' + kind, 'comment': comment, 'initial': initial})

	add('Sim_Clock', 'Integer', 'Simulation: pass counter, stamped with the time of each pass')
	add('Sim_LastMs', 'Real', 'Simulation: time of the previous pass (ms)')
	add('Sim_Dt', 'Real', 'Simulation: time since the previous pass (ms)')
	add('Sim_BlowThru', 'Real', 'Simulation: hopper pressure while air blows through (psig)', 2.0)
	add('Sim_CoilSV102', 'Discrete', 'Simulation: SV-102 coil through CR1')
	for n, _ in SIM_VALVES:
		add('Sim_OpenAcc_' + n, 'Real', 'Simulation: SV-%s opening time (ms)' % n)
		add('Sim_OpenDn_' + n, 'Discrete', 'Simulation: SV-%s open' % n)
		add('Sim_CloseAcc_' + n, 'Real', 'Simulation: SV-%s time since de-energized (ms)' % n, 500)
		add('Sim_CloseDn_' + n, 'Discrete', 'Simulation: SV-%s off its closed seat' % n)
	for i in (1, 2, 3):
		add('Sim_Flow%d' % i, 'Real', 'Simulation: Plast #%d flow into the hopper (lb/s)' % i)
	add('Sim_DischFlow', 'Real', 'Simulation: discharge flow to the mixer (lb/s)')
	add('Sim_DischOpen', 'Discrete', 'Simulation: a discharge valve is off its seat')
	add('Sim_PressTarget', 'Real', 'Simulation: pressure the hopper is heading for (psig)')
	add('Cfg_DefaultsLoaded', 'Discrete', 'Setpoint defaults loaded (kept from one run to the next, like the setpoints)')
	t[-1]['retentive'] = True
	for tag in ('LIT100', 'PT001', 'WT001'):
		add(tag + '_BadAcc', 'Real', '%s signal out of range for (ms)' % tag)
	add('DT2_LowDn', 'Discrete', 'Day tank #2 low longer than the reserve delay')
	add('DT3_LowDn', 'Discrete', 'Day tank #3 low longer than the reserve delay')
	for name, kind, comment in (('PrevStep', 'Integer', 'step in the previous pass'),
			('NextStep', 'Integer', 'step to go to at the end of this pass'),
			('ActStep', 'Integer', 'active step (the held step while held)'),
			('StepInit', 'Discrete', 'first pass in a new step'), ('VentOn', 'Discrete', 'vent requested'),
			('PressOn', 'Discrete', 'air requested'), ('IngDribble', 'Real', 'dribble weight (lb)'),
			('IngPreact', 'Real', 'preact (lb)'), ('IngStartWt', 'Real', 'net weight at ingredient start (lb)'),
			('NoFlowRef', 'Real', 'weight at the last sign of flow (lb)'),
			('NoFlowAcc', 'Real', 'time without flow (ms)'), ('NoFlowDn', 'Discrete', 'no flow'),
			('EmptyAcc', 'Real', 'time below empty pressure (ms)'), ('EmptyDn', 'Discrete', 'hopper empty'),
			('FillActive', 'Discrete', 'a fill valve is open'), ('FillValvesClosed', 'Discrete', 'all fill valves closed')):
		add('Seq_' + name, kind, 'Sequence: ' + comment)
	for v in VALVES:
		add(v + '_AutoCmd', 'Discrete', v + ': open request from the sequence')
		add(v + '_ManCmd', 'Discrete', v + ': open command in Manual')
		add(v + '_TravelAcc', 'Real', v + ': travel time so far (ms)')
		add(v + '_TravelDn', 'Discrete', v + ': travel time passed')
	for p in PUMPS:
		add(p + '_AutoCmd', 'Discrete', p + ': run request from the sequence')
		add(p + '_ManCmd', 'Discrete', p + ': run command in Manual')
		add(p + '_FbkAcc', 'Real', p + ': command and feedback disagree for (ms)')
		add(p + '_FbkDn', 'Discrete', p + ': command and feedback disagree')
		add(p + '_FltFailStart', 'Discrete', p + ': failed to start')
	add('Alm_FlashAcc', 'Real', 'Flasher time (ms)')
	add('Alm_Flash', 'Discrete', '1 Hz flasher')
	for i in range(4):
		add('Alm_OS%d' % i, 'Discrete', 'Alarm one-shot storage')
	return [x for x in t if x['name'] not in LOCAL_NAMES]


RETENTIVE_PREFIXES = ('Cfg_', 'Rcp_TotalVol', 'Rcp_Pct0', 'Rcp_Pct1', 'Rcp_Pct2', 'Bat_Count')
RETENTIVE_SUFFIXES = ('_TravelTime', '_FailTime')
# Starting values that keep the first pass from seeing a fault
INITIAL = {'SV001_Closed': 1, 'SV002_Closed': 1, 'SV102_Manual': 1}
for _n, _ in SIM_VALVES:
	INITIAL['DI_ZSC_' + _n] = 1
for _v in VALVES:
	if _v not in NO_LIMITS:
		INITIAL[_v + '_ZSC'] = 1
		INITIAL[_v + '_Closed'] = 1


def to_memory(tag):
	if not tag['type'].startswith('IO'):
		return tag
	out = {'name': tag['name'], 'type': 'Memory' + tag['type'][2:]}
	for key in ('comment', 'engUnits', 'minEU', 'maxEU', 'onMsg', 'offMsg', 'alarms'):
		if tag.get(key) not in (None, ''):
			out[key] = tag[key]
	out['initial'] = INITIAL.get(tag['name'], '' if tag['type'] == 'IOMessage' else 0)
	if tag['name'].startswith(RETENTIVE_PREFIXES) or tag['name'].endswith(RETENTIVE_SUFFIXES):
		out['retentive'] = True
	return out


def relabel(spec):
	"""Texts that spoke of the PLC."""
	changes = {
		'PLC OK': 'SIM PLC',
		'PROCESS SIMULATION  (in PLC)': 'PROCESS SIMULATION  (in HMI scripts)',
		'The PLC routine R02_Simulation models the process from the outputs when simulation is ON. Turn it OFF to '
		'drive the I/O memory tags by hand from this screen.':
			'There is no PLC: the application\'s While running script runs the controller program, and its '
			'R02 SIMULATION part models the process from the outputs when simulation is ON. Turn it OFF to drive '
			'the inputs by hand from this screen.',
	}
	done = {}

	def walk(x):
		if isinstance(x, dict):
			label = x.get('label')
			if isinstance(label, str) and label in changes:
				x['label'] = changes[label]
				done[label] = done.get(label, 0) + 1
			for v in x.values():
				walk(v)
		elif isinstance(x, list):
			for v in x:
				walk(v)

	walk(spec['pages'])
	return done


def main():
	src, out = sys.argv[1], sys.argv[2]
	spec = json.load(open(src))
	engine = build_script()
	on_start = build_on_start()

	spec['devices'] = []
	spec['tags'] = [to_memory(t) for t in spec['tags']]
	names = {t['name'].lower() for t in spec['tags']}
	for t in new_tags():
		assert t['name'].lower() not in names, t['name']
		spec['tags'].append(t)

	spec['settings']['scripts'] = {'onStart': on_start, 'whileRunning': engine, 'onShutdown': '',
		'everyMs': WHILE_RUNNING_EVERY_MS}

	spec['settings']['publish']['productName'] = 'LiquidWeighHMISim'
	spec['settings']['publish']['output'] = ''
	done = relabel(spec)

	json.dump(spec, open(out, 'w'), indent=1)
	print('While running %d lines, %d bytes; On start %d lines; %d locals; tags %d; relabeled %s'
		% (engine.count('\n'), len(engine), on_start.count('\n'), len(LOCALS), len(spec['tags']), done))


if __name__ == '__main__':
	main()
