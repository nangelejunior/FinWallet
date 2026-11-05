BEGIN TRANSACTION READ ONLY;
SELECT id,type,reason FROM summary_quarantine
WHERE CASE WHEN payload IS JSON THEN jsonb_path_exists(payload::jsonb, '$.**.currency ? (@.type() != "string" || !(@ like_regex "^[A-Z]{3}$"))') ELSE true END;
COMMIT;
