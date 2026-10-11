// A run-only window stops its Run before it closes, so the windows' On hide
// scripts and the application's On shutdown script run, and retentive values
// are saved for the last time. The first allowed close is held while the page
// stops; the page answers hmiRuntime.stopped, or after SHUTDOWN_TIMEOUT_MS the
// window closes anyway. Shared with AppendHMIDesktop.

export const SHUTDOWN_TIMEOUT_MS = 5000;

export function createShutdownState(stopped = false)
{
	return {stopped, timer: null};
}

// From the window's 'close' handler, once the close is allowed. True when the
// close was held for the page to stop.
export function holdClose(win, event, state, timeoutMs = SHUTDOWN_TIMEOUT_MS)
{
	if (state.stopped || win.isDestroyed())
	{
		return false;
	}

	event.preventDefault();

	if (state.timer == null)
	{
		state.timer = setTimeout(() => finishShutdown(win, state), timeoutMs);
		win.webContents.send('hmiRuntimeShutdown');
	}

	return true;
}

// The page has stopped (hmiRuntime.stopped), or it took too long
export function finishShutdown(win, state)
{
	if (state.stopped)
	{
		return;
	}

	state.stopped = true;

	if (state.timer != null)
	{
		clearTimeout(state.timer);
		state.timer = null;
	}

	if (!win.isDestroyed())
	{
		win.close();
	}
}
