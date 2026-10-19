export const MAX_WIRE_BYTES = 512 * 1024;
export const MAX_DECODED_BYTES = 2 * 1024 * 1024; // guards against gzip bombs

export class TooLarge extends Error {}
export class BadEncoding extends Error {}

async function readLimited(stream: ReadableStream<Uint8Array>, limit: number): Promise<Uint8Array> {
  const reader = stream.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > limit) {
      await reader.cancel();
      throw new TooLarge();
    }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let off = 0;
  for (const c of chunks) {
    out.set(c, off);
    off += c.byteLength;
  }
  return out;
}

/**
 * Reads the request body, enforcing the 512 KB wire limit and (for gzip) a 2 MB decoded limit.
 * Throws TooLarge / BadEncoding.
 */
export async function readBody(request: Request): Promise<Uint8Array> {
  const declared = Number(request.headers.get("Content-Length") ?? "0");
  if (declared > MAX_WIRE_BYTES) throw new TooLarge();
  if (!request.body) return new Uint8Array(0);

  const encoding = (request.headers.get("Content-Encoding") ?? "").toLowerCase();
  if (encoding === "" || encoding === "identity") return readLimited(request.body, MAX_WIRE_BYTES);
  if (encoding !== "gzip") throw new BadEncoding();

  const wire = await readLimited(request.body, MAX_WIRE_BYTES);
  try {
    const decoded = new Blob([wire]).stream().pipeThrough(new DecompressionStream("gzip"));
    return await readLimited(decoded, MAX_DECODED_BYTES);
  } catch (e) {
    if (e instanceof TooLarge) throw e;
    throw new BadEncoding();
  }
}
