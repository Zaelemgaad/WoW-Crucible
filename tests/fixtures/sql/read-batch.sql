-- Semicolon inside the string must not split this statement.
SELECT 'Crucible; batch' AS label, COUNT(*) AS item_count FROM item_template;

SELECT entry, name
FROM item_template
WHERE entry IN (17, 17802)
ORDER BY entry;
