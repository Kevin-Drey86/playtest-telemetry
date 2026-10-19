// Applies every migration to a fresh in-memory Postgres (PGlite) and runs the SQL tests.
import { PGlite } from "@electric-sql/pglite";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const db = new PGlite();

const migrations = readdirSync(join(root, "migrations")).filter((f) => f.endsWith(".sql")).sort();
for (const file of migrations) {
  await db.exec(readFileSync(join(root, "migrations", file), "utf8"));
  console.log(`applied ${file}`);
}

// create_project.sql must run and return a key whose hash is stored.
const created = await db.query(readFileSync(join(root, "scripts", "create_project.sql"), "utf8"));
const row = created.rows[0];
if (!row?.api_key_once?.startsWith("pt_")) throw new Error("create_project.sql returned no key");
const stored = await db.query("select api_key_hash from projects where id = $1", [row.project_id]);
const { createHash } = await import("node:crypto");
const expected = createHash("sha256").update(row.api_key_once).digest("hex");
if (stored.rows[0].api_key_hash !== expected) throw new Error("stored hash does not match SHA-256 of key");
console.log("create_project.sql ok");

try {
  await db.exec(readFileSync(join(root, "tests", "ingest_batch.test.sql"), "utf8"));
} catch (e) {
  console.error("TEST FAILED:", e.message, e.where ?? "");
  process.exit(1);
}
console.log("ingest_batch.test.sql ok");
