// SharpInterpreter — Licensed under the MIT License.
// Runs C# scripts in a hidden, sandboxed <iframe> that loads the runner page from its own origin.
//
//   const interpreter = new SharpInterpreter({ runnerUrl: 'https://runner.example.com/' });
//   const result = await interpreter.run('Console.WriteLine(6 * 7);', { 'sales.csv': file });
//   // { Ok, Output, Error, Charts, CompileMs, RunMs }
//
// Scripts run one at a time, in a Web Worker of the runner page; the page ends one that runs too long.
// If the page itself stops answering, the whole iframe is thrown away and the next run loads a fresh one.
(function (global) {
    'use strict';

    const failure = message => ({ Ok: false, Output: '', Error: message, Charts: [], CompileMs: 0, RunMs: 0 });

    async function toBase64(content) {
        if (typeof content === 'string') content = new TextEncoder().encode(content);
        else if (content instanceof Blob) content = new Uint8Array(await content.arrayBuffer());
        else if (content instanceof ArrayBuffer) content = new Uint8Array(content);
        else if (ArrayBuffer.isView(content)) content = new Uint8Array(content.buffer, content.byteOffset, content.byteLength);
        else throw new TypeError('A file must be a string, Blob, ArrayBuffer or typed array.');
        let binary = '';
        for (let i = 0; i < content.length; i += 0x8000)
            binary += String.fromCharCode.apply(null, content.subarray(i, i + 0x8000));
        return btoa(binary);
    }

    class SharpInterpreter {
        /**
         * @param {object} options
         * @param {string} options.runnerUrl  Where the runner is served: the root of an origin other than this page's.
         * @param {number} [options.timeoutMs=30000]       How long a script may run.
         * @param {number} [options.startTimeoutMs=60000]  How long the runner may take to load.
         * @param {HTMLElement} [options.container=document.body]  Where the hidden iframe goes.
         */
        constructor({ runnerUrl, timeoutMs = 30000, startTimeoutMs = 60000, container = document.body }) {
            const url = new URL(runnerUrl, location.href);
            if (url.origin === location.origin)
                throw new Error('The runner must be served from a different origin than this page; otherwise the sandbox is no boundary.');
            this._url = url.href;
            this._origin = url.origin;
            this._timeoutMs = timeoutMs;
            this._startTimeoutMs = startTimeoutMs;
            this._container = container;
            this._frame = null;
            this._ready = null;
            this._starting = null;
            this._pending = new Map();
            this._queue = Promise.resolve();
            this._onMessage = this._onMessage.bind(this);
            window.addEventListener('message', this._onMessage);
        }

        /**
         * Compiles and runs a script.
         * @param {string} code  C# top-level statements.
         * @param {Object<string, string|Blob|ArrayBuffer|Uint8Array>} [files]  What Table.Read / Files.ReadText can open, by name.
         * @returns {Promise<{Ok: boolean, Output: string, Error: ?string, Charts: object[], CompileMs: number, RunMs: number}>}
         */
        run(code, files = {}) {
            const next = this._queue.then(() => this._run(code, files));
            this._queue = next.catch(() => {});
            return next;
        }

        /** Loads the runner now instead of on the first run (it takes a moment). */
        start() {
            return this._ensureStarted();
        }

        /** Removes the iframe and stops listening. */
        dispose() {
            window.removeEventListener('message', this._onMessage);
            this._recycle('The interpreter was disposed.');
        }

        async _run(code, files) {
            const encoded = {};
            for (const [name, content] of Object.entries(files)) encoded[name] = await toBase64(content);
            try {
                await this._ensureStarted();
            } catch (error) {
                return failure(String(error.message || error));
            }

            const id = crypto.randomUUID();
            let timer;
            const result = new Promise(resolve => {
                this._pending.set(id, resolve);
                // The page enforces timeoutMs; this only catches a page that stopped answering.
                timer = setTimeout(() => {
                    this._recycle(null);
                    resolve(failure("The C# runner stopped responding and was restarted."));
                }, this._timeoutMs + 10000);
            });
            this._frame.contentWindow.postMessage({ id, code, files: encoded, timeoutMs: this._timeoutMs }, this._origin);
            try {
                return await result;
            } finally {
                clearTimeout(timer);
                this._pending.delete(id);
            }
        }

        _ensureStarted() {
            if (this._ready) return this._ready;
            const frame = document.createElement('iframe');
            // Scripts, plus its own origin so the runtime can fetch its files. Nothing else: no popups,
            // forms, top navigation, downloads or device features.
            frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
            frame.setAttribute('allow', '');
            frame.setAttribute('referrerpolicy', 'no-referrer');
            frame.setAttribute('aria-hidden', 'true');
            frame.tabIndex = -1;
            frame.title = 'C# runner';
            frame.style.cssText = 'position:absolute;width:1px;height:1px;border:0;opacity:0;pointer-events:none;';
            this._frame = frame;

            const ready = new Promise((resolve, reject) => {
                this._starting = { resolve, reject };
                setTimeout(() => reject(new Error("The C# runner didn't start.")), this._startTimeoutMs);
            });
            ready.then(() => { this._starting = null; }, () => { if (this._frame === frame) this._recycle(null); });
            this._ready = ready;
            // The runner never loads twice: a second load means something navigated the frame away. Drop it.
            let loads = 0;
            frame.addEventListener('load', () => {
                if (++loads > 1 && this._frame === frame) this._recycle('The runner page navigated away and was replaced.');
            });
            frame.src = this._url;
            this._container.appendChild(frame);
            return ready;
        }

        _onMessage(event) {
            if (!this._frame || event.source !== this._frame.contentWindow || event.origin !== this._origin) return;
            const message = event.data;
            if (!message || typeof message !== 'object') return;
            if (message.type === 'ready') this._starting?.resolve();
            else if (message.type === 'failed') this._starting?.reject(new Error(message.error));
            else if (message.type === 'result') {
                const resolve = this._pending.get(message.id);
                if (!resolve) return;
                try {
                    resolve(JSON.parse(message.result));
                } catch {
                    resolve(failure('The runner returned something unreadable.'));
                }
            }
        }

        /** Drops the iframe (and whatever it was running); the next run loads a fresh one. */
        _recycle(reason) {
            if (reason) for (const resolve of this._pending.values()) resolve(failure(reason));
            this._starting?.reject(new Error('The C# runner was restarted.'));
            this._starting = null;
            this._ready = null;
            this._frame?.remove();
            this._frame = null;
        }
    }

    global.SharpInterpreter = SharpInterpreter;
})(typeof window !== 'undefined' ? window : globalThis);
