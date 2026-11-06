// Key-value storage in IndexedDB for Queryeables.Browser (IndexedDbOfflineStorage).
// One database per name, one object store "entries"; values are strings (Base64 from .NET).

const STORE = "entries";
const databases = new Map();

function open(name) {
    let database = databases.get(name);
    if (!database) {
        database = new Promise((resolve, reject) => {
            const request = indexedDB.open(name, 1);
            request.onupgradeneeded = () => request.result.createObjectStore(STORE);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
            request.onblocked = () => reject(new Error(`IndexedDB "${name}" is blocked by another tab.`));
        });
        databases.set(name, database);
        database.catch(() => databases.delete(name));     // the next call tries again
    }
    return database;
}

// Resolves once the transaction is committed, not when the request succeeds: a write is durable when it returns.
async function run(name, mode, action) {
    const database = await open(name);
    return new Promise((resolve, reject) => {
        const transaction = database.transaction(STORE, mode);
        const request = action(transaction.objectStore(STORE));
        transaction.oncomplete = () => resolve(request.result);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB transaction aborted."));
    });
}

export async function get(name, key) {
    const value = await run(name, "readonly", store => store.get(key));
    return value ?? null;
}

export async function set(name, key, value) {
    await run(name, "readwrite", store => store.put(value, key));
}

export async function remove(name, key) {
    await run(name, "readwrite", store => store.delete(key));
}
