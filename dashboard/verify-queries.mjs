// Verifies the dashboards without Grafana: loads the migrations and seed_demo_data.sql into an
// in-memory Postgres (PGlite), then runs every panel query and template-variable query with the
// Grafana variables/macros expanded. Fails on SQL errors, unknown columns or empty results.
//
//   npm test
import { PGlite } from "@electric-sql/pglite";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const dashDir = join(here, "provisioning", "dashboards");
const migDir = join(here, "..", "supabase", "migrations");

const db = new PGlite();
for (const f of readdirSync(migDir).filter((f) => f.endsWith(".sql")).sort()) await db.exec(readFileSync(join(migDir, f), "utf8"));
await db.exec(readFileSync(join(here, "seed_demo_data.sql"), "utf8"));

const dashboards = readdirSync(dashDir).filter((f) => f.endsWith(".json")).map((f) => ({ f, d: JSON.parse(readFileSync(join(dashDir, f), "utf8")) }));

const first = async (sql) => (await db.query(sql)).rows[0];
const projectId = (await first("select id from projects where name = 'Demo Game (seed)'")).id;
const builds = (await db.query("select distinct build_version from v_sessions order by 1")).rows.map((r) => r.build_version);
const sessionId = (await first("select session_id from v_sessions where error_count > 0 order by started_at desc limit 1")).session_id;
const fingerprint = (await first("select fingerprint from v_issues order by total_count desc limit 1")).fingerprint;

// From here on act exactly like Grafana: as the read-only role that can only SELECT the dashboard views.
await db.exec("set role dashboard_reader");

const unit = { h: "hour", d: "day" };
function expand(sql) {
  return sql
    .replaceAll("${build:sqlstring}", builds.map((b) => `'${b}'`).join(","))
    .replace(/\$__timeFilter\(([^)]+)\)/g, "($1 >= now() - interval '30 days' and $1 <= now() + interval '1 day')")
    .replace(/\$__timeGroupAlias\(([^,]+),\s*'1([hd])'\)/g, (_, col, u) => `date_trunc('${unit[u]}', ${col}) as time`)
    .replaceAll("$project", projectId)
    .replaceAll("$session", sessionId)
    .replaceAll("$fingerprint", fingerprint);
}

let failures = 0;
let queries = 0;
const check = async (where, sql, { allowEmpty = false } = {}) => {
  queries++;
  try {
    const rows = (await db.query(expand(sql))).rows;
    if (!rows.length && !allowEmpty) throw new Error("returned no rows");
    console.log(`  ok   ${where}  (${rows.length} row${rows.length === 1 ? "" : "s"})`);
    return rows;
  } catch (e) {
    failures++;
    console.log(`  FAIL ${where}\n       ${e.message}`);
  }
};

for (const { f, d } of dashboards) {
  console.log(`${f}  [uid ${d.uid}]`);
  for (const v of d.templating.list.filter((v) => v.type === "query")) await check(`variable ${v.name}`, v.query);
  for (const p of d.panels) {
    for (const t of p.targets ?? []) await check(`panel "${p.title}"`, t.rawSql);
    // structural sanity for Grafana
    if (!p.gridPos || p.gridPos.w > 24) { failures++; console.log(`  FAIL panel "${p.title}": bad gridPos`); }
    for (const o of p.fieldConfig?.overrides ?? []) {
      // every override that names a column must exist in the query result
      const rows = await db.query(expand(p.targets[0].rawSql));
      const cols = rows.fields.map((x) => x.name);
      if (!cols.includes(o.matcher.options)) {
        failures++;
        console.log(`  FAIL panel "${p.title}": override for unknown column "${o.matcher.options}" (columns: ${cols.join(", ")})`);
      }
    }
  }
}

// Every drill-down link must point at an existing dashboard uid.
const uids = new Set(dashboards.map((x) => x.d.uid));
for (const { f, d } of dashboards) {
  for (const m of JSON.stringify(d).matchAll(/\/d\/([a-z-]+)\?/g)) {
    if (!uids.has(m[1])) { failures++; console.log(`  FAIL ${f}: link to unknown dashboard ${m[1]}`); }
  }
}
if (new Set(dashboards.map((x) => x.d.uid)).size !== dashboards.length) { failures++; console.log("FAIL duplicate dashboard uids"); }

console.log(`\n${queries} queries checked, ${failures} failure(s)`);
process.exit(failures ? 1 : 0);
