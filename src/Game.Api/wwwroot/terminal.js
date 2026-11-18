// The player's terminal: xterm.js connected over a WebSocket to a shell in the
// gate. Started by the GateTerminal component. The server keeps the shell, so
// on reconnect it replays what was on screen.

const sessions = new WeakMap();

export function start(element, status) {
  if (sessions.has(element)) return;

  const say = (text, bad) => {
    status.textContent = text;
    status.classList.toggle('bad', !!bad);
  };

  const term = new Terminal({
    cursorBlink: true,
    fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, Menlo, monospace',
    fontSize: 14,
    scrollback: 5000,
    theme: { background: '#0d100c', foreground: '#d7dccf', cursor: '#d7dccf' },
  });
  const fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  term.open(element);
  fit.fit();

  const encoder = new TextEncoder();
  const session = { term, socket: null, retry: 0, stopped: false, timer: null };
  sessions.set(element, session);

  const sendSize = () => {
    const socket = session.socket;
    if (socket && socket.readyState === WebSocket.OPEN)
      socket.send(JSON.stringify({ type: 'resize', cols: term.cols, rows: term.rows }));
  };

  term.onData(data => {
    const socket = session.socket;
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(encoder.encode(data));
  });
  term.onResize(sendSize);

  // The panel changes size with the window and with the page layout.
  session.observer = new ResizeObserver(() => fit.fit());
  session.observer.observe(element);

  const again = () => {
    if (session.stopped) return;
    session.retry = Math.min(session.retry + 1, 3);
    session.timer = setTimeout(connect, session.retry * 1000);
  };

  async function connect() {
    if (session.stopped) return;
    let state;
    try {
      state = await (await fetch('api/status')).json();
    } catch {
      say('The game is not answering.', true);
      return again();
    }
    if (!state.running) {
      say(state.error ? 'Docker is not answering. Is Docker Desktop running?'
        : state.stopped ? 'The lab is stopped. Start the game again.'
        : 'No lab yet. Start the game.', true);
      return again();
    }

    const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
    const socket = new WebSocket(`${scheme}://${location.host}/terminal`);
    socket.binaryType = 'arraybuffer';
    session.socket = socket;
    socket.onopen = () => {
      session.retry = 0;
      term.reset();
      say(`Run ${state.runId}`);
      sendSize();
      term.focus();
    };
    socket.onmessage = event => term.write(new Uint8Array(event.data));
    socket.onclose = () => {
      if (session.stopped) return;
      say('Disconnected. Reconnecting...', true);
      again();
    };
  }

  connect();
}

export function stop(element) {
  const session = sessions.get(element);
  if (!session) return;
  session.stopped = true;
  clearTimeout(session.timer);
  session.observer?.disconnect();
  session.socket?.close();
  session.term.dispose();
  sessions.delete(element);
}
