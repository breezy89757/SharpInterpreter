// SharpInterpreter — Licensed under the MIT License.
// The Web Worker that runs scripts: { id, code, files } in, { type: 'result', id, result } out.
// A worker has no page to navigate, no DOM and no WebRTC, so a script that reaches JavaScript (it can,
// through System.Runtime.InteropServices.JavaScript) still has no way out but the network, which the
// Content-Security-Policy limits to this site. runner.js ends the worker if a script runs too long.
import { dotnet } from './_framework/dotnet.js';

try {
    const runtime = await dotnet.create();
    const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
    const run = exports.SharpInterpreter.Runner.Interop.Run;

    // Everything the runtime needs is loaded now: take away what could still talk to the server.
    for (const name of ['fetch', 'XMLHttpRequest', 'WebSocket', 'EventSource', 'WebTransport', 'importScripts', 'Worker', 'SharedWorker']) {
        try { Object.defineProperty(self, name, { value: undefined, configurable: false, writable: false }); } catch { }
    }
    try { Object.defineProperty(self.navigator, 'sendBeacon', { value: undefined, configurable: false, writable: false }); } catch { }

    self.onmessage = event => {
        const { id, code, files } = event.data || {};
        let result;
        try {
            result = run(String(code), JSON.stringify(files || {}));
        } catch (error) {
            result = JSON.stringify({ Ok: false, Output: '', Error: String(error && error.message || error), Charts: [], CompileMs: 0, RunMs: 0 });
        }
        self.postMessage({ type: 'result', id, result });
    };
    self.postMessage({ type: 'ready' });
} catch (error) {
    self.postMessage({ type: 'failed', error: String(error && error.message || error) });
}
