-- Private bucket for bug-report screenshots (written by the Worker with the service role).
-- Guarded: only runs where Supabase Storage exists (not on plain Postgres / the PGlite test DB).
do $$
begin
  if to_regclass('storage.buckets') is not null then
    insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
    values ('bug-screenshots', 'bug-screenshots', false, 1048576, array['image/jpeg'])
    on conflict (id) do nothing;
  end if;
end $$;
