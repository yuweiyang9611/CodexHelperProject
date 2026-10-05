import type { Net } from 'electron';
import { Readable } from 'node:stream';

function responseHeaders(values: Record<string, string | string[]>): Headers {
  const headers = new Headers();
  for (const [name, value] of Object.entries(values)) {
    for (const item of Array.isArray(value) ? value : [value]) headers.append(name, item);
  }
  return headers;
}

// Electron's net.fetch rejects manual redirects instead of exposing the 3xx
// response. Keep redirects visible so githubFetch can validate each next URL.
export function createUpdaterFetch(network: Pick<Net, 'request'>): typeof fetch {
  return async (input, options = {}) => {
    if (typeof input !== 'string' && !(input instanceof URL)) throw new TypeError('更新请求必须提供 URL。');
    if (options.body != null || options.method && options.method.toUpperCase() !== 'GET') {
      throw new TypeError('更新请求仅支持 GET。');
    }
    if (options.redirect && options.redirect !== 'manual') throw new TypeError('更新请求必须手动验证重定向。');
    const signal = options.signal;
    signal?.throwIfAborted();
    const request = network.request({
      url: input instanceof URL ? input.href : input,
      method: 'GET',
      headers: Object.fromEntries(new Headers(options.headers)),
      redirect: 'manual',
      credentials: 'omit',
      cache: 'no-store',
    });

    return await new Promise<Response>((resolve, reject) => {
      let settled = false;
      let intentionalAbort = false;
      let networkAborted = false;
      let source: Readable | undefined;
      const removeAbortListener = () => signal?.removeEventListener('abort', abort);
      const abortRequest = () => {
        // Signal cancellation and the Readable close callback can both arrive.
        // Electron's public abort invokes native URLLoader.cancel on every call.
        if (!networkAborted) {
          networkAborted = true;
          request.abort();
        }
      };
      const fail = (reason: Error) => {
        if (!settled) {
          settled = true;
          reject(reason);
        } else if (source && !source.readableEnded && !source.destroyed) {
          source.destroy(reason);
        }
      };
      const abort = () => {
        const reason = signal?.reason ?? new DOMException('更新请求已取消。', 'AbortError');
        fail(reason);
        intentionalAbort = true;
        abortRequest();
        removeAbortListener();
      };
      const resolveWithoutBody = (response: Response) => {
        settled = true;
        resolve(response);
        // No followRedirect: validation belongs to githubFetch. Aborting inside
        // this callback also prevents Chromium from requesting the target.
        intentionalAbort = true;
        abortRequest();
        removeAbortListener();
      };

      request.on('error', error => {
        // Electron's error path already cancels its native loader. The later
        // response close must not cancel that same loader a second time.
        networkAborted = true;
        if (!intentionalAbort) fail(error);
        removeAbortListener();
      });
      request.on('abort', () => {
        networkAborted = true;
        if (!intentionalAbort) fail(new DOMException('更新请求已取消。', 'AbortError'));
        removeAbortListener();
      });
      // ClientRequest is a Writable: its close can precede response headers.
      // Response end/error/close owns the body lifetime and signal cleanup.
      request.once('redirect', (status, _method, location, values) => {
        try {
          const headers = responseHeaders(values);
          if (!headers.has('location')) headers.set('location', location);
          resolveWithoutBody(new Response(null, { status, headers }));
        } catch (reason) {
          fail(reason instanceof Error ? reason : new Error('更新重定向响应无效。'));
          intentionalAbort = true;
          abortRequest();
          removeAbortListener();
        }
      });
      request.once('response', response => {
        try {
          const headers = responseHeaders(response.headers);
          if ([204, 205, 304].includes(response.statusCode)) {
            resolveWithoutBody(new Response(null, { status: response.statusCode, headers }));
            return;
          }
          // Electron's implementation inherits Node Readable, although its
          // public declaration only lists EventEmitter. Validate the runtime
          // seam before relying on toWeb's backpressure and cancellation.
          if (!(response instanceof Readable)) throw new Error('更新响应不支持流式读取。');
          source = response as unknown as Readable;
          source.once('end', removeAbortListener);
          source.once('error', removeAbortListener);
          source.once('close', () => {
            removeAbortListener();
            if (!source?.readableEnded) {
              intentionalAbort = true;
              abortRequest();
            }
          });
          response.once('aborted', () => fail(new DOMException('更新响应已取消。', 'AbortError')));
          const body = Readable.toWeb(source, {
            strategy: { highWaterMark: 64 * 1024, size: (chunk: Uint8Array) => chunk.byteLength },
          }) as ReadableStream<Uint8Array>;
          const result = new Response(body, { status: response.statusCode, headers });
          settled = true;
          resolve(result);
        } catch (reason) {
          fail(reason instanceof Error ? reason : new Error('更新响应无效。'));
          intentionalAbort = true;
          abortRequest();
          removeAbortListener();
        }
      });
      signal?.addEventListener('abort', abort, { once: true });
      // The signal can change between the initial check and listener setup.
      if (signal?.aborted) abort();
      else request.end();
    });
  };
}
