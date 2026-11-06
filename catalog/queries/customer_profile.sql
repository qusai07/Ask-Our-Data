WITH paid AS (
    SELECT p.invoice_id, SUM(p.amount) AS paid_amount
    FROM rpt.payment_v AS p
    WHERE p.customer_id = @customer_id
      AND p.payment_date <= @as_of
    GROUP BY p.invoice_id
),
bal AS (
    SELECT SUM(i.amount - ISNULL(pd.paid_amount, 0)) AS open_balance,
           SUM(CASE WHEN i.due_date < @as_of THEN i.amount - ISNULL(pd.paid_amount, 0) ELSE 0 END) AS overdue_balance
    FROM rpt.invoice_v AS i
    LEFT JOIN paid AS pd ON pd.invoice_id = i.invoice_id
    WHERE i.customer_id = @customer_id
      AND i.status NOT IN ('void', 'credited')
      AND i.issue_date <= @as_of
      AND i.amount - ISNULL(pd.paid_amount, 0) > 0
)
SELECT TOP (1)
       c.customer_id,
       c.customer_code,
       c.customer_name,
       c.region_name,
       c.segment_code,
       c.status,
       CAST(c.payment_terms_days AS int)                 AS payment_terms_days,
       c.credit_limit,
       c.created_at                                      AS customer_since,
       ct.contact_name_masked                            AS primary_contact_name,
       ct.email_masked                                   AS primary_contact_email,
       ct.phone_masked                                   AS primary_contact_phone,
       CAST(ISNULL(b2.open_balance, 0) AS decimal(18, 2))    AS open_balance,
       CAST(ISNULL(b2.overdue_balance, 0) AS decimal(18, 2)) AS overdue_balance,
       bk.bank_name,
       bk.iban_last4
FROM rpt.customer_v AS c
LEFT JOIN rpt.customer_contact_v AS ct ON ct.customer_id = c.customer_id AND ct.is_primary = 1
LEFT JOIN rpt.customer_bank_v    AS bk ON bk.customer_id = c.customer_id
CROSS JOIN bal AS b2
WHERE c.customer_id = @customer_id
  AND c.region_id IN (SELECT TRY_CAST([value] AS tinyint) FROM OPENJSON(@region_filter))
