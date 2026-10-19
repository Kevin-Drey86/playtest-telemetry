// A tiny stand-in for Supabase REST, for running the Worker locally (`wrangler dev`) without a
// Supabase project. It accepts any API key hash, records every ingest_batch payload and answers
// like the real function would.
//
//   node tools/fake-supabase.mjs [port=54321] [captureFile=captured-batches.jsonl]
//   npx wrangler dev --var SUPABASE_URL:http://127.0.0.1:54321 --var SUPABASE_SERVICE_ROLE_KEY:local
import { appendFileSync } from "node:fs";
import { createServer } from "node:http";

const port = Number(process.argv[2] ?? 54321);
const captureFile = process.argv[3] ?? "captured-batches.jsonl";

createServer((req, res) => {
  const chunks = [];
  req.on("data", (c) => chunks.push(c));
  req.on("end", () => {
    const body = Buffer.concat(chunks).toString("utf8");
    const send = (status, json) => {
      res.writeHead(status, { "Content-Type": "application/json" });
      res.end(JSON.stringify(json));
    };
    if (req.method === "GET" && req.url.startsWith("/rest/v1/projects")) {
      return send(200, [{ id: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", name: "Local Test", discord_webhook_url: null }]);
    }
    if (req.method === "POST" && req.url.startsWith("/rest/v1/rpc/ingest_batch")) {
      appendFileSync(captureFile, body + "\n");
      const payload = JSON.parse(body).p_payload;
      const n = (payload.events?.length ?? 0) + (payload.perf_samples?.length ?? 0) + (payload.bug_reports?.length ?? 0);
      console.log(`ingest_batch: ${payload.events.length} events, ${payload.perf_samples.length} perf, ${payload.bug_reports.length} bug reports`);
      return send(200, { inserted: n, new_issues: [] });
    }
    if (req.url.startsWith("/storage/")) return send(200, {});
    send(404, { message: "not found" });
  });
}).listen(port, "127.0.0.1", () => console.log(`fake supabase on :${port}, capturing to ${captureFile}`));
