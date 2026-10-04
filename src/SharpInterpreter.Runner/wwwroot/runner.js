// SharpInterpreter — Licensed under the MIT License.
// The runner page: relays { id, code, files, timeoutMs } from the host to a Web Worker that runs the
// script (worker.js), and { type: 'result', id, result } back. Scripts never run on this page, so it
// stays responsive: when one runs too long, the worker is ended and a fresh one started.
// Hosts: an <iframe> (messages from window.parent only; replies go back to the sender's origin) or a
// WebView2 (chrome.webview).
const webview = window.chrome && window.chrome.webview;
const send = (message, origin) => webview ? webview.postMessage(message) : window.parent.postMessage(message, origin);
const failure = error => JSON.stringify({ Ok: false, Output: '', Error: error, Charts: [], CompileMs: 0, RunMs: 0 });
const defaultTimeoutMs = 30000;

let worker = null;     // the current worker
let ready = null;      // resolves when it has loaded .NET
let running = null;    // { id, origin, timer } of the script it's running

function startWorker() {
    const current = new Worker('worker.js', { type: 'module' });
    worker = current;
    ready = new Promise((resolve, reject) => {
        current.onmessage = event => {
            const message = event.data || {};
            if (message.type === 'ready') resolve();
            else if (message.type === 'failed') reject(new Error(message.error));
            else if (message.type === 'result' && running && message.id === running.id) finish(String(message.result));
        };
        current.onerror = event => reject(new Error(event.message || 'The C# runtime failed to load.'));
    });
    return ready;
}

function finish(result) {
    const { id, origin, timer } = running;
    clearTimeout(timer);
    running = null;
    send({ type: 'result', id, result }, origin);
}

async function onMessage(data, origin) {
    if (!data || typeof data !== 'object' || typeof data.code !== 'string') return;
    if (running) {
        send({ type: 'result', id: data.id, result: failure('Another script is still running.') }, origin);
        return;
    }
    const timeoutMs = Number.isFinite(data.timeoutMs) && data.timeoutMs > 0 ? data.timeoutMs : defaultTimeoutMs;
    running = { id: data.id, origin, timer: 0 };
    try {
        await ready;
    } catch (error) {
        finish(failure(String(error.message || error)));
        startWorker().catch(() => { });
        return;
    }
    running.timer = setTimeout(() => {
        // The worker is stuck in the script: end it and load a fresh one for the next run.
        worker.terminate();
        finish(failure(`The script ran longer than ${Math.round(timeoutMs / 1000)} seconds and was stopped.`));
        startWorker().catch(() => { });
    }, timeoutMs);
    worker.postMessage({ id: data.id, code: data.code, files: data.files || {} });
}

startWorker().then(
    // Nothing secret in these two, and the parent's origin isn't known yet.
    () => send({ type: 'ready' }, '*'),
    error => send({ type: 'failed', error: String(error.message || error) }, '*'));

if (webview) {
    webview.addEventListener('message', event => onMessage(event.data, null));
} else {
    // Who may embed this page is the server's call (CSP frame-ancestors); here, only the embedder is heard.
    window.addEventListener('message', event => {
        if (event.source === window.parent && window.parent !== window) onMessage(event.data, event.origin);
    });
}
