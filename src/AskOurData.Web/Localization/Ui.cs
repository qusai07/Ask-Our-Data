using System.Globalization;
using System.Text.RegularExpressions;

namespace AskOurData.Web.Localization;

/// <summary>UI strings for English and Arabic. Keys missing in Arabic fall back to English.</summary>
public sealed partial class Ui(string lang)
{
    public string Lang { get; } = lang == "ar" ? "ar" : "en";
    public bool Rtl => Lang == "ar";
    public string Dir => Rtl ? "rtl" : "ltr";

    public static readonly CultureInfo NumberCulture = CultureInfo.GetCultureInfo("en-US");   // Western digits in both languages

    public string this[string key] =>
        (Rtl && Ar.TryGetValue(key, out var a)) ? a : En.TryGetValue(key, out var e) ? e : key;

    public string F(string key, params object[] args) => string.Format(CultureInfo.InvariantCulture, this[key], args);

    public string Report(string tool) => this["report." + tool];
    public string Column(string col) => (Rtl ? Ar : En).TryGetValue("col." + col, out var v) ? v : En.GetValueOrDefault("col." + col, col);
    public string Role(string role) => (Rtl ? Ar : En).TryGetValue("role." + role, out var v) ? v : role;
    public string Definition(string key, string fallback) => (Rtl ? Ar : En).TryGetValue("def." + key, out var v) ? v : fallback;
    public IReadOnlyList<string> Examples(string tool) =>
        (Rtl ? ExamplesAr : ExamplesEn).TryGetValue(tool, out var v) ? v : [];

    /// <summary>"FY27 Q3 (to date)" → "الربع 3 من السنة المالية 27 (حتى تاريخه)"; month names translated.</summary>
    public string PeriodLabel(string label)
    {
        if (!Rtl) return label;
        var s = label.Replace(" (to date)", " (حتى تاريخه)").Replace("Last 12 months", "آخر 12 شهرًا")
            .Replace("All dates up to", "كل التواريخ حتى").Replace("As of", "كما في").Replace(" to ", " إلى ");
        s = FyQuarter().Replace(s, m => $"الربع {m.Groups[2].Value} من السنة المالية {m.Groups[1].Value}");
        s = Fy().Replace(s, m => $"السنة المالية {m.Groups[1].Value}");
        foreach (var (en, ar) in Months) s = s.Replace(en, ar);
        return s;
    }

    [GeneratedRegex(@"FY(\d{2}) Q(\d)")] private static partial Regex FyQuarter();
    [GeneratedRegex(@"FY(\d{2})")] private static partial Regex Fy();

    private static readonly (string, string)[] Months =
    [
        ("January", "يناير"), ("February", "فبراير"), ("March", "مارس"), ("April", "أبريل"), ("May", "مايو"), ("June", "يونيو"),
        ("July", "يوليو"), ("August", "أغسطس"), ("September", "سبتمبر"), ("October", "أكتوبر"), ("November", "نوفمبر"), ("December", "ديسمبر"),
    ];

    private static readonly Dictionary<string, string> En = new()
    {
        ["app.name"] = "Ask our data",
        ["app.tagline"] = "Answers from approved reports only",
        ["nav.signout"] = "Sign out",
        ["nav.language"] = "العربية",
        ["nav.language.aria"] = "Switch to Arabic",
        ["nav.newchat"] = "New conversation",
        ["nav.reports"] = "What can I ask?",
        ["nav.closereports"] = "Close",

        ["login.title"] = "Sign in",
        ["login.subtitle"] = "Development mode: choose a demo user. In production you sign in with your company account.",
        ["login.as"] = "Continue as {0}",
        ["login.prod"] = "Sign in with your company account",
        ["login.user.fiona"] = "Finance · all regions",
        ["login.user.rita"] = "Finance · APAC only",
        ["login.user.sam"] = "Sales manager · EMEA",
        ["login.user.erin"] = "Executive · all regions",
        ["login.user.nora"] = "No report roles",
        ["login.user.ian"] = "Inactive account",
        ["login.user.ghost"] = "Not registered",

        ["home.greeting"] = "Hello, {0}",
        ["home.intro"] = "Ask a question about customers, invoices, payments or revenue. I answer only from approved reports and show you the period, definitions and data freshness.",
        ["home.try"] = "Try one of these",
        ["home.disabled"] = "Your account is not enabled for reports. Contact your administrator.",
        ["home.gatewaydown"] = "The report service is not reachable right now. Please try again shortly.",

        ["composer.placeholder"] = "Ask about customers, invoices, payments or revenue…",
        ["composer.send"] = "Send",
        ["composer.stop"] = "Stop",
        ["composer.hint"] = "Enter to send · Shift+Enter for a new line",

        ["progress.thinking"] = "Understanding your question…",
        ["progress.tool"] = "Running report: {0}",
        ["progress.checking"] = "Checking every figure against the data…",

        ["answer.period"] = "Period",
        ["answer.scope"] = "Regions",
        ["answer.freshness"] = "Data as of",
        ["answer.rows"] = "{0} rows",
        ["answer.truncated"] = "Showing the first {0} rows. Ask for a smaller set or a higher limit (max 100).",
        ["answer.total"] = "Total",
        ["answer.definitions"] = "Definitions used",
        ["answer.trace"] = "How this answer was produced",
        ["answer.trace.rows"] = "{0} rows",
        ["answer.copy"] = "Copy table",
        ["answer.copied"] = "Copied",
        ["answer.csv"] = "Download CSV",
        ["answer.withheld"] = "The figures are in the table below. (The written summary was withheld because it could not be verified against the data.)",
        ["answer.default.metric_amount"] = "Ranked by late amount (default). Ask \"by count\" or \"by days late\" to change.",
        ["answer.masked"] = "Hidden or masked for your role",
        ["answer.untrusted"] = "Text from business records, shown as-is",
        ["answer.empty"] = "No matching records for this period and scope.",
        ["answer.time"] = "{0:0.0}s",

        ["kind.clarify"] = "One quick question",
        ["kind.outofscope.title"] = "No report for that yet",
        ["kind.outofscope.body"] = "I can't answer this with the approved reports, so I won't guess. I've logged it as a report request for the reporting team.",
        ["kind.denied.title"] = "Not available for your role",
        ["kind.denied.body"] = "You don't have access to this report or data scope.",
        ["kind.notfound.title"] = "Not found",
        ["kind.notfound.body"] = "That record wasn't found within the data you can access.",
        ["kind.error.title"] = "Something went wrong",
        ["kind.error.body"] = "The question couldn't be answered. Please try again or rephrase it.",
        ["kind.retry"] = "Try again",
        ["kind.seereports"] = "See what I can answer",

        ["reports.title"] = "Reports you can use",
        ["reports.subtitle"] = "Only reports allowed for your role are listed.",
        ["reports.regions"] = "Your regions",

        ["role.finance"] = "Finance",
        ["role.sales_manager"] = "Sales manager",
        ["role.executive"] = "Executive",

        ["report.list_available_reports"] = "Available reports",
        ["report.find_customer"] = "Find customer",
        ["report.get_top_customers_by_late_payments"] = "Top customers by late payments",
        ["report.get_overdue_invoices"] = "Overdue invoices",
        ["report.get_ar_aging_summary"] = "AR aging summary",
        ["report.get_revenue_by_customer"] = "Revenue by customer",
        ["report.get_customer_profile"] = "Customer profile",
        ["report.get_collections_summary"] = "Collections summary",
        ["report.ask_clarifying_question"] = "Clarifying question",
        ["report.report_not_available"] = "No matching report",

        ["col.rank"] = "#", ["col.customer_id"] = "Customer ID", ["col.customer_code"] = "Code", ["col.customer_name"] = "Customer",
        ["col.region_code"] = "Region", ["col.region_name"] = "Region", ["col.segment_code"] = "Segment",
        ["col.late_invoice_count"] = "Late invoices", ["col.late_amount"] = "Late amount", ["col.avg_days_late"] = "Avg days late",
        ["col.invoice_number"] = "Invoice", ["col.issue_date"] = "Issued", ["col.due_date"] = "Due", ["col.amount"] = "Amount",
        ["col.open_balance"] = "Open balance", ["col.days_overdue"] = "Days overdue", ["col.notes"] = "Notes",
        ["col.group_label"] = "Group", ["col.current_amount"] = "Current", ["col.days_1_30"] = "1–30", ["col.days_31_60"] = "31–60",
        ["col.days_61_90"] = "61–90", ["col.days_90_plus"] = "90+", ["col.total_open"] = "Total open",
        ["col.invoice_count"] = "Invoices", ["col.revenue"] = "Revenue", ["col.status"] = "Status",
        ["col.payment_terms_days"] = "Terms (days)", ["col.credit_limit"] = "Credit limit", ["col.customer_since"] = "Customer since",
        ["col.primary_contact_name"] = "Contact", ["col.primary_contact_email"] = "Email", ["col.primary_contact_phone"] = "Phone",
        ["col.overdue_balance"] = "Overdue balance", ["col.bank_name"] = "Bank", ["col.iban_last4"] = "IBAN (last 4)",
        ["col.payment_count"] = "Payments", ["col.cash_collected"] = "Cash collected", ["col.avg_days_to_pay"] = "Avg days to pay",
        ["col.name"] = "Report", ["col.description"] = "What it answers",
    };

    private static readonly Dictionary<string, string> Ar = new()
    {
        ["app.name"] = "اسأل بياناتنا",
        ["app.tagline"] = "إجابات من التقارير المعتمدة فقط",
        ["nav.signout"] = "تسجيل الخروج",
        ["nav.language"] = "English",
        ["nav.language.aria"] = "التبديل إلى الإنجليزية",
        ["nav.newchat"] = "محادثة جديدة",
        ["nav.reports"] = "ماذا يمكنني أن أسأل؟",
        ["nav.closereports"] = "إغلاق",

        ["login.title"] = "تسجيل الدخول",
        ["login.subtitle"] = "وضع التطوير: اختر مستخدمًا تجريبيًا. في بيئة الإنتاج يتم الدخول بحساب الشركة.",
        ["login.as"] = "المتابعة باسم {0}",
        ["login.prod"] = "الدخول بحساب الشركة",
        ["login.user.fiona"] = "المالية · كل المناطق",
        ["login.user.rita"] = "المالية · آسيا والمحيط الهادئ فقط",
        ["login.user.sam"] = "مدير مبيعات · أوروبا والشرق الأوسط وأفريقيا",
        ["login.user.erin"] = "تنفيذي · كل المناطق",
        ["login.user.nora"] = "بدون أدوار تقارير",
        ["login.user.ian"] = "حساب غير مفعّل",
        ["login.user.ghost"] = "غير مسجّل",

        ["home.greeting"] = "مرحبًا، {0}",
        ["home.intro"] = "اسأل عن العملاء أو الفواتير أو المدفوعات أو الإيرادات. أجيب من التقارير المعتمدة فقط، وأوضح لك الفترة والتعريفات وتاريخ تحديث البيانات.",
        ["home.try"] = "جرّب أحد هذه الأسئلة",
        ["home.disabled"] = "حسابك غير مفعّل للتقارير. يرجى التواصل مع المسؤول.",
        ["home.gatewaydown"] = "خدمة التقارير غير متاحة حاليًا. يرجى المحاولة بعد قليل.",

        ["composer.placeholder"] = "اسأل عن العملاء أو الفواتير أو المدفوعات أو الإيرادات…",
        ["composer.send"] = "إرسال",
        ["composer.stop"] = "إيقاف",
        ["composer.hint"] = "Enter للإرسال · Shift+Enter لسطر جديد",

        ["progress.thinking"] = "جارٍ فهم سؤالك…",
        ["progress.tool"] = "تشغيل التقرير: {0}",
        ["progress.checking"] = "التحقق من كل رقم مقابل البيانات…",

        ["answer.period"] = "الفترة",
        ["answer.scope"] = "المناطق",
        ["answer.freshness"] = "البيانات حتى",
        ["answer.rows"] = "{0} صفوف",
        ["answer.truncated"] = "يتم عرض أول {0} صفوف. اطلب مجموعة أصغر أو حدًا أعلى (الحد الأقصى 100).",
        ["answer.total"] = "الإجمالي",
        ["answer.definitions"] = "التعريفات المستخدمة",
        ["answer.trace"] = "كيف تم إعداد هذه الإجابة",
        ["answer.trace.rows"] = "{0} صفوف",
        ["answer.copy"] = "نسخ الجدول",
        ["answer.copied"] = "تم النسخ",
        ["answer.csv"] = "تنزيل CSV",
        ["answer.withheld"] = "الأرقام موجودة في الجدول أدناه. (تم حجب الملخص المكتوب لأنه تعذّر التحقق منه مقابل البيانات.)",
        ["answer.default.metric_amount"] = "الترتيب حسب مبلغ التأخير (افتراضي). اطلب \"حسب العدد\" أو \"حسب أيام التأخير\" للتغيير.",
        ["answer.masked"] = "مخفي أو مُقنّع لدورك",
        ["answer.untrusted"] = "نص من سجلات العمل، يُعرض كما هو",
        ["answer.empty"] = "لا توجد سجلات مطابقة لهذه الفترة والنطاق.",
        ["answer.time"] = "{0:0.0} ث",

        ["kind.clarify"] = "سؤال سريع",
        ["kind.outofscope.title"] = "لا يوجد تقرير لهذا بعد",
        ["kind.outofscope.body"] = "لا يمكنني الإجابة على هذا من التقارير المعتمدة، ولن أخمّن. تم تسجيله كطلب تقرير لفريق التقارير.",
        ["kind.denied.title"] = "غير متاح لدورك",
        ["kind.denied.body"] = "ليست لديك صلاحية على هذا التقرير أو نطاق البيانات.",
        ["kind.notfound.title"] = "غير موجود",
        ["kind.notfound.body"] = "لم يتم العثور على هذا السجل ضمن البيانات المتاحة لك.",
        ["kind.error.title"] = "حدث خطأ",
        ["kind.error.body"] = "تعذّرت الإجابة على السؤال. يرجى المحاولة مرة أخرى أو إعادة الصياغة.",
        ["kind.retry"] = "إعادة المحاولة",
        ["kind.seereports"] = "اعرض ما يمكنني الإجابة عنه",

        ["reports.title"] = "التقارير المتاحة لك",
        ["reports.subtitle"] = "تظهر فقط التقارير المسموح بها لدورك.",
        ["reports.regions"] = "مناطقك",

        ["role.finance"] = "المالية",
        ["role.sales_manager"] = "مدير مبيعات",
        ["role.executive"] = "تنفيذي",

        ["report.list_available_reports"] = "التقارير المتاحة",
        ["report.find_customer"] = "البحث عن عميل",
        ["report.get_top_customers_by_late_payments"] = "أكثر العملاء تأخرًا في السداد",
        ["report.get_overdue_invoices"] = "الفواتير المتأخرة",
        ["report.get_ar_aging_summary"] = "ملخص أعمار الذمم المدينة",
        ["report.get_revenue_by_customer"] = "الإيرادات حسب العميل",
        ["report.get_customer_profile"] = "ملف العميل",
        ["report.get_collections_summary"] = "ملخص التحصيل",
        ["report.ask_clarifying_question"] = "سؤال توضيحي",
        ["report.report_not_available"] = "لا يوجد تقرير مطابق",

        ["col.rank"] = "#", ["col.customer_id"] = "رقم العميل", ["col.customer_code"] = "الرمز", ["col.customer_name"] = "العميل",
        ["col.region_code"] = "المنطقة", ["col.region_name"] = "المنطقة", ["col.segment_code"] = "الشريحة",
        ["col.late_invoice_count"] = "فواتير متأخرة", ["col.late_amount"] = "مبلغ التأخير", ["col.avg_days_late"] = "متوسط أيام التأخير",
        ["col.invoice_number"] = "الفاتورة", ["col.issue_date"] = "تاريخ الإصدار", ["col.due_date"] = "تاريخ الاستحقاق", ["col.amount"] = "المبلغ",
        ["col.open_balance"] = "الرصيد المفتوح", ["col.days_overdue"] = "أيام التأخير", ["col.notes"] = "ملاحظات",
        ["col.group_label"] = "المجموعة", ["col.current_amount"] = "غير مستحق", ["col.days_1_30"] = "1–30", ["col.days_31_60"] = "31–60",
        ["col.days_61_90"] = "61–90", ["col.days_90_plus"] = "+90", ["col.total_open"] = "إجمالي المفتوح",
        ["col.invoice_count"] = "عدد الفواتير", ["col.revenue"] = "الإيرادات", ["col.status"] = "الحالة",
        ["col.payment_terms_days"] = "شروط الدفع (أيام)", ["col.credit_limit"] = "حد الائتمان", ["col.customer_since"] = "عميل منذ",
        ["col.primary_contact_name"] = "جهة الاتصال", ["col.primary_contact_email"] = "البريد", ["col.primary_contact_phone"] = "الهاتف",
        ["col.overdue_balance"] = "الرصيد المتأخر", ["col.bank_name"] = "البنك", ["col.iban_last4"] = "IBAN (آخر 4)",
        ["col.payment_count"] = "عدد الدفعات", ["col.cash_collected"] = "المبالغ المحصّلة", ["col.avg_days_to_pay"] = "متوسط أيام السداد",
        ["col.name"] = "التقرير", ["col.description"] = "ماذا يجيب",

        ["def.currency"] = "جميع المبالغ بالدولار الأمريكي.",
        ["def.late_invoice"] = "الفاتورة المتأخرة = فاتورة سُددت بالكامل بعد تاريخ استحقاقها بأكثر من 0 يوم، أو ما زالت غير مسددة بعد تاريخ استحقاقها حتى تاريخ التقرير.",
        ["def.days_late"] = "أيام التأخير = (تاريخ السداد الكامل، أو تاريخ التقرير إن لم تُسدد) ناقص تاريخ الاستحقاق.",
        ["def.late_amount"] = "مبلغ التأخير = إجمالي مبالغ الفواتير المتأخرة.",
        ["def.period_by_due_date"] = "تُنسب الفواتير إلى الفترة حسب تاريخ استحقاقها.",
        ["def.excludes_void_credited"] = "تُستبعد الفواتير الملغاة والمقيدة دائنًا.",
        ["def.open_balance"] = "الرصيد المفتوح = مبلغ الفاتورة ناقص الدفعات المستلمة حتى تاريخ التقرير.",
        ["def.overdue"] = "متأخرة = رصيد مفتوح أكبر من 0 وتاريخ استحقاق قبل تاريخ التقرير.",
        ["def.aging_buckets"] = "فئات الأعمار حسب أيام التأخير على الرصيد المفتوح: غير مستحق، 1-30، 31-60، 61-90، أكثر من 90 يومًا.",
        ["def.revenue_invoiced"] = "الإيرادات = المبالغ المفوترة (أساس الفوترة وليس النقد) للفواتير الصادرة خلال الفترة.",
        ["def.cash_collected"] = "المبالغ المحصّلة = الدفعات التي تاريخ سدادها ضمن الفترة.",
        ["def.days_to_pay"] = "متوسط أيام السداد = متوسط (تاريخ الدفع ناقص تاريخ إصدار الفاتورة) عبر الدفعات.",
        ["def.contact_masking"] = "بيانات الاتصال مُقنّعة. تظهر بيانات البنك (اسم البنك وآخر 4 أحرف من IBAN) فقط للأدوار المسموح لها.",
        ["def.customer_search"] = "يطابق العملاء الذين يحتوي اسمهم على نص البحث (دون حساسية لحالة الأحرف)، ضمن نطاق بياناتك.",
        ["def.totals_shown_rows"] = "يحسب الخادم الإجماليات على الصفوف المعروضة.",
    };

    private static readonly Dictionary<string, string[]> ExamplesEn = new()
    {
        ["get_top_customers_by_late_payments"] = ["Who were our top 5 customers by late payments this quarter?", "Top 10 late payers last fiscal year by count"],
        ["get_ar_aging_summary"] = ["Show AR aging by region", "Aging by customer as of the end of last quarter"],
        ["get_revenue_by_customer"] = ["Top 10 customers by revenue this fiscal year", "Enterprise revenue leaders last quarter"],
        ["get_overdue_invoices"] = ["Which invoices are overdue for Harbor Logistics?"],
        ["get_customer_profile"] = ["Show me the profile of Summit Foods"],
        ["get_collections_summary"] = ["Cash collected by month this fiscal year"],
        ["find_customer"] = ["Find customers named Cedar"],
        ["list_available_reports"] = ["What can you answer?"],
    };

    private static readonly Dictionary<string, string[]> ExamplesAr = new()
    {
        ["get_top_customers_by_late_payments"] = ["من هم أكثر 5 عملاء تأخرًا في السداد هذا الربع؟", "أكثر 10 عملاء تأخرًا في السنة المالية الماضية حسب العدد"],
        ["get_ar_aging_summary"] = ["اعرض أعمار الذمم المدينة حسب المنطقة", "أعمار الذمم حسب العميل كما في نهاية الربع الماضي"],
        ["get_revenue_by_customer"] = ["أعلى 10 عملاء من حيث الإيرادات هذه السنة المالية", "أعلى عملاء الشريحة الكبرى إيرادًا في الربع الماضي"],
        ["get_overdue_invoices"] = ["ما الفواتير المتأخرة لشركة Harbor Logistics؟"],
        ["get_customer_profile"] = ["اعرض ملف العميل Summit Foods"],
        ["get_collections_summary"] = ["المبالغ المحصّلة حسب الشهر هذه السنة المالية"],
        ["find_customer"] = ["ابحث عن العملاء باسم Cedar"],
        ["list_available_reports"] = ["ماذا يمكنك أن تجيب؟"],
    };
}
