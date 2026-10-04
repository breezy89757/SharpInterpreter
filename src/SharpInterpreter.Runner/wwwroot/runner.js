// SharpInterpreter — Licensed under the MIT License.
// Bridge between the host page and the .NET runtime in this page: { id, code, files } in, { type: 'result', id, result } out.
// Hosts: an <iframe> (messages from window.parent only; replies go back to the sender's origin) or a
// WebView2 (chrome.webview). Scripts run synchronously on this page's only thread; the host replaces
// the page if one runs too long.
// WebRTC can reach the network without being subject to CSP: remove it before any .NET code runs.
for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCSessionDescription', 'RTCIceCandidate']) {
    try { delete window[name]; } catch { }
}

const webview = window.chrome && window.chrome.webview;
const send = (message, origin) => webview ? webview.postMessage(message) : window.parent.postMessage(message, origin);

const started = Blazor.start();
started.then(
    // Nothing secret in these two, and the parent's origin isn't known yet.
    () => send({ type: 'ready' }, '*'),
    error => send({ type: 'failed', error: String(error) }, '*'));

async function onMessage(data, origin) {
    if (!data || typeof data !== 'object' || typeof data.code !== 'string') return;
    const { id, code, files } = data;
    await started;
    let result;
    try {
        result = DotNet.invokeMethod('SharpInterpreter.Runner', 'Run', code, files || {});
    } catch (error) {
        result = JSON.stringify({ Ok: false, Output: '', Error: String(error && error.message || error), Charts: [], CompileMs: 0, RunMs: 0 });
    }
    send({ type: 'result', id, result }, origin);
}

if (webview) {
    webview.addEventListener('message', event => onMessage(event.data, null));
} else {
    // Who may embed this page is the server's call (CSP frame-ancestors); here, only the embedder is heard.
    window.addEventListener('message', event => {
        if (event.source === window.parent && window.parent !== window) onMessage(event.data, event.origin);
    });
}
