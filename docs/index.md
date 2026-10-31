---
_layout: landing
title: Queryeables
---

<style>
/* Landing page styles: kept with the page, so that it renders even without the site template. */
:root { --qy-teal: #0284C7; --qy-teal-bright: #0EA5E9; --qy-mint: #7DD3FC; --qy-deep: #1E3A8A; --qy-night: #050B1F; }
/* ---------- Hero ---------- */
.qy-hero {
  margin: -1rem calc(50% - 50vw) 0;
  padding: clamp(3rem, 8vw, 6rem) 1.25rem clamp(2.5rem, 6vw, 4.5rem);
  color: #fff;
  background:
    radial-gradient(56rem 26rem at 8% -12%, rgba(14, 165, 233, .55), transparent 60%),
    radial-gradient(42rem 26rem at 100% 115%, rgba(30, 58, 138, .95), transparent 62%),
    linear-gradient(180deg, #0A1A3A, var(--qy-night));
  position: relative;
  overflow: hidden;
}
.qy-hero::before {
  /* a faint grid, like a table */
  content: ""; position: absolute; inset: 0; pointer-events: none;
  background-image: linear-gradient(rgba(125, 211, 252, .06) 1px, transparent 1px),
                    linear-gradient(90deg, rgba(125, 211, 252, .06) 1px, transparent 1px);
  background-size: 44px 44px;
  mask-image: radial-gradient(ellipse at 30% 30%, #000 30%, transparent 75%);
}
.qy-hero-inner { position: relative; max-width: 70rem; margin: 0 auto; display: grid; gap: 2.5rem; grid-template-columns: 1fr; align-items: center; }
@media (min-width: 992px) { .qy-hero-inner { grid-template-columns: 1fr 1.05fr; } }
.qy-kicker { display: inline-block; font-size: .78rem; font-weight: 700; letter-spacing: .12em; text-transform: uppercase;
  color: var(--qy-mint); border: 1px solid rgba(125, 211, 252, .35); border-radius: 999px; padding: .25rem .75rem; margin-bottom: 1.1rem; }
.qy-hero h1 { color: #fff; font-size: clamp(2.2rem, 5vw, 3.7rem); line-height: 1.04; margin: 0 0 1rem; font-weight: 800; }
.qy-hero h1 em { font-style: normal; background: linear-gradient(90deg, var(--qy-mint), #BAE6FD); -webkit-background-clip: text; background-clip: text; color: transparent; }
.qy-hero p { color: #cfe3ff; font-size: 1.12rem; max-width: 34rem; margin: 0 0 1.75rem; }
.qy-cta { display: flex; flex-wrap: wrap; gap: .75rem; }
.qy-cta a { padding: .7rem 1.25rem; border-radius: 999px; font-weight: 650; text-decoration: none; transition: background .15s, transform .15s; }
.qy-cta a:hover { transform: translateY(-1px); }
.qy-cta .qy-primary { background: var(--qy-mint); color: #08214d; }
.qy-cta .qy-primary:hover { background: #BAE6FD; }
.qy-cta .qy-ghost { border: 1px solid rgba(255, 255, 255, .4); color: #fff; }
.qy-cta .qy-ghost:hover { background: rgba(255, 255, 255, .1); }
/* ---------- Code with Local / Remote tabs (CSS only) ---------- */
.qy-code { background: #050d24; border: 1px solid rgba(125, 211, 252, .18); border-radius: 14px; overflow: hidden;
  box-shadow: 0 30px 70px -28px rgba(0, 0, 0, .85), 0 0 0 1px rgba(255, 255, 255, .02) inset; }
.qy-code input { position: absolute; opacity: 0; pointer-events: none; }
.qy-tabs { display: flex; gap: .25rem; padding: .45rem .55rem 0; border-bottom: 1px solid rgba(255, 255, 255, .08); }
.qy-tabs label { cursor: pointer; padding: .4rem .85rem; border-radius: 8px 8px 0 0; font-size: .82rem; font-weight: 600; color: #93acd1; }
.qy-tabs label:hover { color: #fff; }
#qy-local:checked ~ .qy-tabs label[for="qy-local"],
#qy-remote:checked ~ .qy-tabs label[for="qy-remote"] { color: #fff; background: rgba(125, 211, 252, .12); box-shadow: inset 0 -2px 0 var(--qy-mint); }
#qy-local:focus-visible ~ .qy-tabs label[for="qy-local"],
#qy-remote:focus-visible ~ .qy-tabs label[for="qy-remote"] { outline: 2px solid var(--qy-mint); outline-offset: -2px; }
.qy-pane { display: none; }
#qy-local:checked ~ .qy-panes .qy-local, #qy-remote:checked ~ .qy-panes .qy-remote { display: block; }
.qy-code pre, .qy-pre { margin: 0; padding: 1rem 1.1rem; background: transparent; border: 0; color: #e6efff; font-size: .86rem; line-height: 1.6; overflow-x: auto; }
.qy-code .c { color: #7b8fb5; } .qy-code .k { color: #7dd3fc; } .qy-code .s { color: #a7f3d0; } .qy-code .t { color: #fcd34d; }
.qy-code .hl { display: inline-block; width: 100%; background: rgba(125, 211, 252, .13); border-left: 3px solid var(--qy-mint); margin-left: -1.1rem; padding-left: calc(1.1rem - 3px); }
.qy-code footer { padding: .55rem 1rem; font-size: .8rem; color: #93acd1; border-top: 1px solid rgba(255, 255, 255, .08); }
/* ---------- Animated flow: database → caches → clients ---------- */
.qy-flow { max-width: 70rem; margin: 2.5rem auto 0; position: relative; }
.qy-flow svg { width: 100%; height: auto; display: block; }
.qy-flow .node { fill: rgba(255, 255, 255, .04); stroke: rgba(125, 211, 252, .45); stroke-width: 1.5; }
.qy-flow .label { fill: #e6efff; font: 600 14px system-ui, sans-serif; }
.qy-flow .sub { fill: #93acd1; font: 12px system-ui, sans-serif; }
.qy-flow .wire { stroke: rgba(125, 211, 252, .3); stroke-width: 2; fill: none; stroke-dasharray: 4 6; }
.qy-flow .dot { fill: var(--qy-mint); filter: drop-shadow(0 0 6px rgba(125, 211, 252, .9)); }
/* ---------- Below the hero ---------- */
.qy-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: 1rem; margin: 2.75rem 0 2.5rem; }
.qy-stats div { border: 1px solid var(--bs-border-color); border-radius: 12px; padding: 1rem 1.15rem; }
.qy-stats b { display: block; font-size: 1.9rem; line-height: 1.1; font-weight: 800; color: var(--bs-link-color); }
.qy-stats span { color: var(--bs-secondary-color); font-size: .92rem; }
.qy-providers { display: flex; flex-wrap: wrap; gap: .6rem; margin: 0 0 3rem; padding: 0; list-style: none; }
.qy-providers li { margin: 0; }
.qy-providers a { display: block; padding: .45rem 1rem; border: 1px solid var(--bs-border-color); border-radius: 999px; font-weight: 600; text-decoration: none; }
.qy-providers a:hover { border-color: var(--qy-teal-bright); background: rgba(14, 165, 233, .08); }
.qy-grid { display: grid; gap: 1.75rem 2.5rem; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); margin-bottom: 3rem; }
.qy-grid h3 { margin: 0 0 .35rem; font-size: 1.1rem; }
.qy-grid p { margin: 0; color: var(--bs-secondary-color); }
.qy-grid div { border-top: 3px solid var(--qy-teal-bright); padding-top: .9rem; }
.qy-fit { display: grid; gap: 1.25rem; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); margin-bottom: 3rem; }
.qy-fit > div { border-radius: 12px; padding: 1.1rem 1.25rem; border: 1px solid var(--bs-border-color); }
.qy-fit .yes { background: rgba(14, 165, 233, .07); }
.qy-fit h3 { font-size: 1.05rem; margin: 0 0 .5rem; }
.qy-fit ul { margin: 0; padding-left: 1.1rem; }
.qy-fit li { margin: .25rem 0; }
@media (prefers-reduced-motion: no-preference) {
  .qy-hero-inner > * { animation: qy-in .6s ease-out both; }
  .qy-hero-inner > *:nth-child(2) { animation-delay: .12s; }
  .qy-flow { animation: qy-in .7s .25s ease-out both; }
}
@keyframes qy-in { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
/* The flow diagram needs width to stay readable. */
@media (max-width: 720px) { .qy-flow { display: none; } }
</style>


<div class="qy-hero"><div class="qy-hero-inner">
<div>
<span class="qy-kicker">.NET 10 · local or remote · AOT-ready</span>
<h1>Your data, <em>the same code</em> everywhere.</h1>
<p>One store API over SQLite, PostgreSQL, SQL Server, EF Core or a remote server. Caches that follow the database, parent-child graphs saved in one transaction, and change notifications between clients.</p>
<div class="qy-cta">
<a class="qy-primary" href="getting-started.md">Get started</a>
<a class="qy-ghost" href="introduction.md">Is it for you?</a>
<a class="qy-ghost" href="https://github.com/cedric56/Queryeables">GitHub</a>
</div>
</div>
<div class="qy-code">
<input type="radio" name="qy-mode" id="qy-local" checked>
<input type="radio" name="qy-mode" id="qy-remote">
<div class="qy-tabs"><label for="qy-local">Local database</label><label for="qy-remote">Remote server</label></div>
<div class="qy-panes">
<div class="qy-pane qy-local"><pre class="qy-pre"><span class="k">[EntityCache]</span>                         <span class="c">// follows the database</span>
public class <span class="t">Person</span>
{
    public long Id { get; set; }
    <span class="k">[EntityGraph]</span> public List&lt;<span class="t">Contact</span>&gt;? Contacts { get; set; }
}
<span></span>
services.UseQueryeables(q =&gt; q
<span class="hl">    .UseSqlite(<span class="s">"Data Source=app.db"</span>)</span>
    .AddGeneratedEntities());
<span></span>
<span class="k">var</span> page = <span class="k">await</span> stores.Query&lt;<span class="t">Person</span>&gt;()
    .Where(p =&gt; p.Name.StartsWith(search))
    .OrderBy(p =&gt; p.Name).Skip(<span class="s">50</span>).Take(<span class="s">25</span>)
    .ToListAsync();</pre></div>
<div class="qy-pane qy-remote"><pre class="qy-pre"><span class="k">[EntityCache]</span>                         <span class="c">// follows the server</span>
public class <span class="t">Person</span>
{
    public long Id { get; set; }
    <span class="k">[EntityGraph]</span> public List&lt;<span class="t">Contact</span>&gt;? Contacts { get; set; }
}
<span></span>
services.UseQueryeables(q =&gt; q
<span class="hl">    .UseMagicOnion(<span class="s">"https://server:5001"</span>)</span>
    .AddGeneratedEntities());
<span></span>
<span class="k">var</span> page = <span class="k">await</span> stores.Query&lt;<span class="t">Person</span>&gt;()
    .Where(p =&gt; p.Name.StartsWith(search))
    .OrderBy(p =&gt; p.Name).Skip(<span class="s">50</span>).Take(<span class="s">25</span>)
    .ToListAsync();                  <span class="c">// sorted and paged by the server</span></pre></div>
</div>
<footer>One line changes. The rest of the application does not.</footer>
</div>
</div>
<div class="qy-flow" aria-hidden="true">
<svg viewBox="0 0 1000 170" xmlns="http://www.w3.org/2000/svg"><style>.node{fill:rgba(255,255,255,.04);stroke:rgba(125,211,252,.45);stroke-width:1.5}.label{fill:#e6efff;font:600 14px system-ui,sans-serif}.sub{fill:#93acd1;font:12px system-ui,sans-serif}.wire{stroke:rgba(125,211,252,.3);stroke-width:2;fill:none;stroke-dasharray:4 6}.dot{fill:#7DD3FC}</style>
<path id="qy-w1" class="wire" d="M230 85 H400"/>
<path id="qy-w2" class="wire" d="M600 85 C680 85 700 40 790 40"/>
<path id="qy-w3" class="wire" d="M600 85 C680 85 700 130 790 130"/>
<rect class="node" x="10" y="45" width="220" height="80" rx="14"/>
<text class="label" x="120" y="80" text-anchor="middle">Database</text>
<text class="sub" x="120" y="102" text-anchor="middle">SQLite · PostgreSQL · SQL Server</text>
<rect class="node" x="400" y="45" width="200" height="80" rx="14"/>
<text class="label" x="500" y="80" text-anchor="middle">Server</text>
<text class="sub" x="500" y="102" text-anchor="middle">policies · graphs · caches</text>
<rect class="node" x="790" y="10" width="190" height="60" rx="12"/>
<text class="label" x="885" y="37" text-anchor="middle">Client cache</text>
<text class="sub" x="885" y="56" text-anchor="middle">desktop · mobile</text>
<rect class="node" x="790" y="100" width="190" height="60" rx="12"/>
<text class="label" x="885" y="127" text-anchor="middle">Client cache</text>
<text class="sub" x="885" y="146" text-anchor="middle">browser · AOT</text>
<text class="sub" x="315" y="72" text-anchor="middle">row changed</text>
<text class="sub" x="695" y="88" text-anchor="middle">notified</text>
<circle class="dot" r="5"><animateMotion dur="2.4s" repeatCount="indefinite"><mpath href="#qy-w1"/></animateMotion></circle>
<circle class="dot" r="5"><animateMotion dur="2.4s" begin="1.2s" repeatCount="indefinite"><mpath href="#qy-w2"/></animateMotion></circle>
<circle class="dot" r="5"><animateMotion dur="2.4s" begin="1.2s" repeatCount="indefinite"><mpath href="#qy-w3"/></animateMotion></circle>
</svg>
</div>
</div>

<div class="qy-stats">
<div><b>4×</b><span>faster reads by key than Dommel with the same predicate (262 → 64 µs)</span></div>
<div><b>1 line</b><span>from a local database to a remote server</span></div>
<div><b>0</b><span>SQL or expression trees over the wire: a whitelist of operators</span></div>
<div><b>9</b><span>compile-time diagnostics for the attributes (QRY001–QRY009)</span></div>
</div>

<ul class="qy-providers">
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.Sqlite/README.md">SQLite</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.PostgreSql/README.md">PostgreSQL</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.Dommel/README.md">SQL Server · MySQL (Dommel)</a></li>
<li><a href="https://github.com/cedric56/Queryeables/blob/master/Queryeables.EntityFramework/README.md">EF Core</a></li>
<li><a href="guides/remote-access.md">MagicOnion</a></li>
<li><a href="guides/change-notifications.md">SignalR</a></li>
</ul>

<div class="qy-grid">
<div><h3>One store API</h3><p>CRUD, filters with <code>&amp;&amp;</code>, <code>||</code> and <code>!</code>, sorting, paging, projections and transactions: identical on Dommel, EF Core and a remote server, proven by the same contract tests.</p></div>
<div><h3>Caches that follow the database</h3><p><code>[EntityCache]</code>: row by row from SQLite hooks, PostgreSQL <code>LISTEN/NOTIFY</code> or SQL Server; bursts coalesced, incremental resync after a reconnection.</p></div>
<div><h3>Entity graphs</h3><p><code>[EntityGraph]</code>: a parent saved, removed and loaded with its children in one transaction, navigation properties set, lookups found in their caches.</p></div>
<div><h3>Results, not exceptions</h3><p>Transactions return a <code>EntityResult</code> with an error kind: <code>Validation</code>, <code>Conflict</code>, <code>NotFound</code>, <code>Forbidden</code>.</p></div>
<div><h3>Remote without coupling</h3><p>The remote core speaks to <code>IEntityTransport</code>; MagicOnion is one transport. An access policy per exposed type, validation on the server.</p></div>
<div><h3>Generated, AOT-ready</h3><p>A source generator writes the graph repositories and the registration; a Native AOT smoke app is published and run by the CI.</p></div>
</div>

<div class="qy-fit">
<div class="yes"><h3>Choose Queryeables for</h3><ul>
<li>an application whose data is local today and shared through a server tomorrow;</li>
<li>clients that must see each other's changes;</li>
<li>parent-child aggregates saved as one unit.</li>
</ul></div>
<div><h3>Choose something else for</h3><ul>
<li>large offline-first local databases: <a href="https://github.com/CommunityToolkit/Datasync">Datasync</a>;</li>
<li>fine-grained reactive computations: <a href="https://github.com/ActualLab/Fusion">Fusion</a>;</li>
<li>joins and reporting: EF Core or Dapper, next to Queryeables.</li>
</ul></div>
</div>

- [Introduction](introduction.md): the model, and an honest comparison.
- [Getting started](getting-started.md): a working application in five steps.
- [Guides](guides/stores-and-transactions.md): stores, queries, graphs, caches, remote access, notifications, [offline mode](guides/offline-mode.md), security.
- [Benchmarks](benchmarks.md): what the abstraction costs, measured.
- [API reference](api/Queryeables.html): every public type.

> **Status:** early. The API may still change before 1.0.
