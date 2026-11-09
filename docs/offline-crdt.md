# Offline CRDT primitives

The offline package now exposes deterministic state-based CRDT building blocks in
`Queryeables.Remote.Offline`:

- `CrdtClock` / `CrdtStamp`: Lamport ordering, with stable replica and operation
  identifiers as deterministic tie-breakers. Wall-clock time is deliberately not
  used to decide which write wins.
- `CrdtRegister<T>`: last-writer-wins register, including an explicit deletion
  marker. Its merge is associative, commutative and idempotent.
- `CrdtCounter`: PN-counter whose merge takes the maximum component per replica.
- `CrdtSet<T>`: observed-remove set. A concurrent add survives a remove that did
  not observe its add-tag.

## Automatic conflict policy (A)

For scalar fields, the intended automatic policy is LWW register merge. For
numeric counters use PN-counter; for unordered membership use OR-Set. This avoids
a blanket "last HTTP request wins" rule and makes duplicate/out-of-order state
delivery safe for these data types.

## Important integration boundary

These are merge primitives, not yet a wire protocol or a replacement for the
existing `OfflineMode.GoOnlineAsync` flow. The current `IEntityTransport`
write contract sends a serialized whole-entity payload and expects a server
`WriteOnceAsync` implementation. Correct end-to-end convergence requires the
server to persist/merge CRDT metadata and operation identities as well as the
client. A SignalR notification alone is not durable state. Do not advertise
automatic conflict resolution as enabled until both ends exchange and persist
the same CRDT envelope.

OR-Set removal tags are tombstones. They must not be compacted until all replicas
that may later reconnect have acknowledged the removal (or an explicit replica
retention policy has expired them). Persist each replica's ID and Lamport counter
across restarts.
