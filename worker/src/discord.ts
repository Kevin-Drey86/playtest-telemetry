import type { CleanSession, NewIssue, Project } from "./types";

const MAX_EMBEDS = 10;
const WEBHOOK_RE = /^https:\/\/(?:discord|discordapp)\.com\/api\/webhooks\/\d+\/[\w-]+$/;
const COLORS: Record<string, number> = { Exception: 0xe74c3c, Error: 0xe67e22, Warning: 0xf1c40f };

/**
 * Sends ONE Discord message for all new issues of a batch (max 10 embeds).
 * Never throws and never retries: a Discord 429 or any failure simply drops the alert.
 */
export async function sendNewIssueAlert(project: Project, session: CleanSession, issues: NewIssue[]): Promise<void> {
  const url = project.discord_webhook_url;
  if (!url || issues.length === 0) return;
  if (!WEBHOOK_RE.test(url)) {
    console.warn("discord webhook url rejected: unexpected host/format");
    return;
  }

  const shown = issues.slice(0, MAX_EMBEDS);
  const embeds = shown.map((i) => ({
    title: `New ${i.log_type}: ${i.message_template}`.slice(0, 256),
    color: COLORS[i.log_type] ?? 0x95a5a6,
    fields: [
      { name: "Project", value: project.name.slice(0, 100), inline: true },
      { name: "Build", value: (i.build || "?").slice(0, 100), inline: true },
      { name: "Type", value: i.log_type, inline: true },
      { name: "Device", value: session.device_model || "?", inline: true },
      { name: "Tester", value: session.tester_alias ?? "(none)", inline: true },
      { name: "Session", value: "`" + session.id + "`", inline: false },
    ],
  }));

  const body = {
    content: issues.length > shown.length ? `+${issues.length - shown.length} more new issues` : undefined,
    embeds,
    allowed_mentions: { parse: [] as string[] }, // message text is client-controlled: never ping anyone
  };

  try {
    const res = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(5000),
    });
    if (res.status === 429) console.warn("discord rate limited: alert dropped");
    else if (!res.ok) console.warn(`discord webhook failed: ${res.status}`);
  } catch (e) {
    console.warn(`discord webhook error: ${(e as Error).message}`);
  }
}
