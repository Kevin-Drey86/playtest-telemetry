-- Creates a project and prints its API key ONCE. Only the SHA-256 hash is stored.
-- Run in the Supabase SQL editor (or psql). Edit the name / webhook / retention below first.
-- Copy the `api_key_once` value immediately: it cannot be recovered afterwards.
-- To rotate a key later, see docs/runbook.md.

with k as (
  select 'pt_' || replace(gen_random_uuid()::text, '-', '') || replace(gen_random_uuid()::text, '-', '') as raw
),
ins as (
  insert into public.projects (name, api_key_hash, discord_webhook_url, retention_days)
  select
    'Puzzle Scout 2',                                   -- project name
    encode(sha256(convert_to(k.raw, 'UTF8')), 'hex'),
    null,                                        -- Discord webhook URL, e.g. 'https://discord.com/api/webhooks/...'
    30                                           -- retention_days
  from k
  returning id, name
)
select ins.id as project_id, ins.name, k.raw as api_key_once
from ins, k;
