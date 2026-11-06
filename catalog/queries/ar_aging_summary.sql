WITH paid AS (
    SELECT p.invoice_id, SUM(p.amount) AS paid_amount
    FROM rpt.payment_v AS p
    WHERE p.payment_date <= @as_of
      AND p.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
    GROUP BY p.invoice_id
),
open_inv AS (
    SELECT i.customer_id,
           i.due_date,
           i.amount - ISNULL(pd.paid_amount, 0) AS open_balance
    FROM rpt.invoice_v AS i
    LEFT JOIN paid AS pd ON pd.invoice_id = i.invoice_id
    WHERE i.status NOT IN ('void', 'credited')
      AND i.issue_date <= @as_of
      AND i.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
      AND i.amount - ISNULL(pd.paid_amount, 0) > 0
),
labeled AS (
    SELECT CASE @group_by
               WHEN 'customer' THEN c.customer_name
               WHEN 'segment'  THEN s.segment_name
               ELSE c.region_name
           END                                AS group_label,
           DATEDIFF(day, o.due_date, @as_of)  AS dpd,
           o.open_balance
    FROM open_inv AS o
    JOIN rpt.customer_v AS c ON c.customer_id = o.customer_id
    JOIN rpt.segment_v  AS s ON s.segment_id  = c.segment_id
)
SELECT TOP (@limit_plus_one)
       group_label,
       CAST(SUM(CASE WHEN dpd <= 0               THEN open_balance ELSE 0 END) AS decimal(18, 2)) AS current_amount,
       CAST(SUM(CASE WHEN dpd BETWEEN 1  AND 30  THEN open_balance ELSE 0 END) AS decimal(18, 2)) AS days_1_30,
       CAST(SUM(CASE WHEN dpd BETWEEN 31 AND 60  THEN open_balance ELSE 0 END) AS decimal(18, 2)) AS days_31_60,
       CAST(SUM(CASE WHEN dpd BETWEEN 61 AND 90  THEN open_balance ELSE 0 END) AS decimal(18, 2)) AS days_61_90,
       CAST(SUM(CASE WHEN dpd > 90               THEN open_balance ELSE 0 END) AS decimal(18, 2)) AS days_90_plus,
       CAST(SUM(open_balance) AS decimal(18, 2))                                                  AS total_open
FROM labeled
GROUP BY group_label
ORDER BY total_open DESC, group_label
