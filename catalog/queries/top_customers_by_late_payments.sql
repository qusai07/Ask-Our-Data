WITH paid AS (
    SELECT p.invoice_id,
           SUM(p.amount)       AS paid_amount,
           MAX(p.payment_date) AS last_payment_date
    FROM rpt.payment_v AS p
    WHERE p.payment_date <= @as_of
      AND p.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
    GROUP BY p.invoice_id
),
inv AS (
    SELECT i.customer_id,
           i.amount,
           DATEDIFF(day, i.due_date,
                    COALESCE(CASE WHEN ISNULL(pd.paid_amount, 0) >= i.amount THEN pd.last_payment_date END, @as_of)
           ) AS days_late
    FROM rpt.invoice_v AS i
    LEFT JOIN paid AS pd ON pd.invoice_id = i.invoice_id
    WHERE i.status NOT IN ('void', 'credited')
      AND i.due_date BETWEEN @period_from AND @period_to
      AND i.due_date < @as_of
      AND i.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
),
agg AS (
    SELECT customer_id,
           COUNT(*)                                          AS late_invoice_count,
           CAST(SUM(amount) AS decimal(18, 2))               AS late_amount,
           CAST(AVG(CAST(days_late AS decimal(9, 1))) AS decimal(9, 1)) AS avg_days_late
    FROM inv
    WHERE days_late > 0
    GROUP BY customer_id
)
SELECT TOP (@limit_plus_one)
       CAST(ROW_NUMBER() OVER (
            ORDER BY CASE @metric
                         WHEN 'count'         THEN CAST(a.late_invoice_count AS decimal(18, 2))
                         WHEN 'avg_days_late' THEN CAST(a.avg_days_late AS decimal(18, 2))
                         ELSE a.late_amount
                     END DESC,
                     a.customer_id) AS int) AS [rank],
       a.customer_id,
       c.customer_name,
       c.region_code,
       a.late_invoice_count,
       a.late_amount,
       a.avg_days_late
FROM agg AS a
JOIN rpt.customer_v AS c ON c.customer_id = a.customer_id
ORDER BY [rank]
