import { contextBridge, ipcRenderer } from 'electron';

const actions = new Set(['ready', 'refresh', 'open', 'todos', 'expand', 'lock']);
contextBridge.exposeInMainWorld('codexUSurface', Object.freeze({
  action(name: string): Promise<unknown> {
    if (!actions.has(name)) return Promise.reject(new Error('Unsupported surface action.'));
    return ipcRenderer.invoke('codexu:surface', name);
  },
  subscribe(listener: (data: unknown) => void): () => void {
    if (typeof listener !== 'function') throw new TypeError('Listener required.');
    const handler = (_event: unknown, value: unknown) => listener(value);
    ipcRenderer.on('codexu:surface-data', handler);
    return () => ipcRenderer.removeListener('codexu:surface-data', handler);
  },
}));
