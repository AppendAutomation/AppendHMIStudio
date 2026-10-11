// A run-only window stops its Run before it closes — src/main/runtime/RuntimeShutdown.js
import { test, describe } from 'node:test';
import assert from 'node:assert/strict';
import { createShutdownState, holdClose, finishShutdown } from '../main/runtime/RuntimeShutdown.js';

function fakeWindow()
{
	return {
		sent: [],
		closes: 0,
		destroyed: false,
		isDestroyed() { return this.destroyed; },
		close() { this.closes++; },
		webContents: { send(channel) { win.sent.push(channel); } }
	};
}

let win;

function fakeEvent()
{
	return { prevented: false, preventDefault() { this.prevented = true; } };
}

describe('runtime shutdown', () =>
{
	test('the first close is held and the page told to stop', () =>
	{
		win = fakeWindow();
		const state = createShutdownState();
		const e = fakeEvent();

		assert.equal(holdClose(win, e, state, 10000), true);
		assert.equal(e.prevented, true);
		assert.deepEqual(win.sent, ['hmiRuntimeShutdown']);

		// A second close while the page stops is held without asking again
		const e2 = fakeEvent();
		assert.equal(holdClose(win, e2, state, 10000), true);
		assert.equal(e2.prevented, true);
		assert.deepEqual(win.sent, ['hmiRuntimeShutdown']);

		finishShutdown(win, state);
		assert.equal(state.stopped, true);
		assert.equal(state.timer, null);
		assert.equal(win.closes, 1);

		// Then the window closes
		const e3 = fakeEvent();
		assert.equal(holdClose(win, e3, state), false);
		assert.equal(e3.prevented, false);

		finishShutdown(win, state);
		assert.equal(win.closes, 1);
	});

	test('a page that does not answer is closed after the timeout', async () =>
	{
		win = fakeWindow();
		const state = createShutdownState();

		holdClose(win, fakeEvent(), state, 20);
		await new Promise((resolve) => setTimeout(resolve, 60));

		assert.equal(state.stopped, true);
		assert.equal(win.closes, 1);
	});

	test('a window already stopped, or destroyed, is not held', () =>
	{
		win = fakeWindow();
		const e = fakeEvent();
		assert.equal(holdClose(win, e, createShutdownState(true)), false);
		assert.equal(e.prevented, false);

		win.destroyed = true;
		const state = createShutdownState();
		assert.equal(holdClose(win, fakeEvent(), state), false);
		finishShutdown(win, state);
		assert.equal(win.closes, 0);
		assert.deepEqual(win.sent, []);
	});
});
