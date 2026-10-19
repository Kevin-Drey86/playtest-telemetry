// Replays payloads captured from the Worker (tools/fake-supabase.mjs) through the real ingest_batch.
// Usage: node tests/replay.mjs <captured.jsonl>
import { PGlite } from "@electric-sql/pglite";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const db = new PGlite();
for (const f of readdirSync(join(root, "migrations")).filter((f) => f.endsWith(".sql")).sort()) {
  await db.exec(readFileSync(join(root, "migrations", f), "utf8"));
}
const project = (await db.query("insert into projects (name, api_key_hash) values ('Replay', 'x') returning id")).rows[0].id;

const lines = readFileSync(process.argv[2], "utf8").split("\n").filter(Boolean);
for (const line of lines) {
  const { p_payload } = JSON.parse(line);
  const r = await db.query("select ingest_batch($1, $2::jsonb) as r", [project, JSON.stringify(p_payload)]);
  console.log("ingest_batch ->", JSON.stringify(r.rows[0].r));
}
for (const t of ["sessions", "events", "perf_samples", "bug_reports", "issues"]) {
  const rows = (await db.query(`select * from ${t}`)).rows;
  console.log(`\n${t} (${rows.length})`);
  for (const row of rows) {
    const short = Object.fromEntries(Object.entries(row).map(([k, v]) => [k, typeof v === "string" && v.length > 70 ? v.slice(0, 70) + "…" : v]));
    console.log(" ", JSON.stringify(short));
  }
}
