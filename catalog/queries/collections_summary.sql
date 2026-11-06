WITH pay AS (
    SELECT CASE @group_by
               WHEN 'region'  THEN r.region_name
               WHEN 'segment' THEN s.segment_name
               ELSE CONVERT(char(7), p.payment_date, 126)
           END                                         AS group_label,
           p.amount,
           DATEDIFF(day, i.issue_date, p.payment_date) AS days_to_pay
    FROM rpt.payment_v AS p
    JOIN rpt.invoice_v AS i ON i.invoice_id = p.invoice_id
    JOIN rpt.region_v  AS r ON r.region_id  = p.region_id
    JOIN rpt.segment_v AS s ON s.segment_id = i.segment_id
    WHERE p.payment_date BETWEEN @period_from AND @period_to
      AND p.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
)
SELECT TOP (@limit_plus_one)
       group_label,
       COUNT(*)                                                       AS payment_count,
       CAST(SUM(amount) AS decimal(18, 2))                            AS cash_collected,
       CAST(AVG(CAST(days_to_pay AS decimal(9, 1))) AS decimal(9, 1)) AS avg_days_to_pay
FROM pay
GROUP BY group_label
ORDER BY group_label
