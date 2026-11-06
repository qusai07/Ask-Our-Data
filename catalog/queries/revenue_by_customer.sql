SELECT TOP (@limit_plus_one)
       CAST(ROW_NUMBER() OVER (ORDER BY SUM(i.amount) DESC, c.customer_id) AS int) AS [rank],
       c.customer_id,
       c.customer_name,
       c.region_code,
       c.segment_code,
       COUNT(*)                            AS invoice_count,
       CAST(SUM(i.amount) AS decimal(18, 2)) AS revenue
FROM rpt.invoice_v AS i
JOIN rpt.customer_v AS c ON c.customer_id = i.customer_id
WHERE i.status NOT IN ('void', 'credited')
  AND i.issue_date BETWEEN @period_from AND @period_to
  AND i.region_id  IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
  AND i.segment_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@segment_filter))
GROUP BY c.customer_id, c.customer_name, c.region_code, c.segment_code
ORDER BY [rank]
