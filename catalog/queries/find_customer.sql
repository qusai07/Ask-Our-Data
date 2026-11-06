SELECT TOP (@limit_plus_one)
       c.customer_id,
       c.customer_code,
       c.customer_name,
       c.region_code,
       c.segment_code
FROM rpt.customer_v AS c
WHERE c.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
  AND c.customer_name LIKE @name_query ESCAPE N'\'
ORDER BY CASE WHEN c.customer_name = @name_query_exact THEN 0 ELSE 1 END,
         c.customer_name,
         c.customer_id
