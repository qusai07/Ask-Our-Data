WITH paid AS (
    SELECT p.invoice_id, SUM(p.amount) AS paid_amount
    FROM rpt.payment_v AS p
    WHERE p.customer_id = @customer_id
      AND p.payment_date <= @as_of
    GROUP BY p.invoice_id
)
SELECT TOP (@limit_plus_one)
       i.invoice_number,
       c.customer_name,
       i.issue_date,
       i.due_date,
       i.amount,
       CAST(i.amount - ISNULL(pd.paid_amount, 0) AS decimal(18, 2)) AS open_balance,
       DATEDIFF(day, i.due_date, @as_of)                           AS days_overdue,
       i.notes
FROM rpt.invoice_v AS i
JOIN rpt.customer_v AS c ON c.customer_id = i.customer_id
LEFT JOIN paid AS pd ON pd.invoice_id = i.invoice_id
WHERE i.customer_id = @customer_id
  AND i.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
  AND i.status NOT IN ('void', 'credited')
  AND i.issue_date <= @as_of
  AND i.due_date < @as_of
  AND i.due_date BETWEEN @period_from AND @period_to
  AND i.amount - ISNULL(pd.paid_amount, 0) > 0
ORDER BY days_overdue DESC, i.invoice_number
