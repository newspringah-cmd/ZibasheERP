using ZibasheERP.API.AddressLabels;
using ZibasheERP.Domain.Entities;

public static class ShippingAddressDisplayTests
{
    public static void Run(Action<bool,string> check)
    {
        check(ShippingAddressDisplay.NormalizeName("نَرگِس  اَمـيني") == "نرگس امینی","address header names normalize Arabic yeh, diacritics, tatweel and repeated spaces");
        check(ShippingAddressDisplay.NormalizeName("آرش كريمى") == "آرش کریمی","name display preserves alef madda while normalizing Arabic kaf and final yeh forms");
        check(ShippingAddressDisplay.NormalizeName("نرگس ام\u200fینی") == "نرگس امینی"&&ShippingAddressDisplay.NormalizeName("نرگس ام\u200dینی")=="نرگس امینی","invisible direction and joining controls cannot split the searchable recipient name");
        var raw="خراسان رضوی\nمشهد خیابان نمونه پلاک ۱\nنام گیرنده: نرگس اميني\nکدپستی: 0123456789\n09120000000";
        var address=new Address{ReceiverName="@recipient",FullAddress=raw};
        check(ShippingAddressDisplay.RecipientName(address)=="نرگس امینی","explicit postal recipient is used instead of a username placeholder or a province in the raw address");
        var original="📦 درخواست ارسال جدید\n\nآیدی: @recipient\n\n"+raw+"\n\n📦 آیتم‌های آماده ارسال\n🔴 تعداد آیتم‌ها: 1";
        var updated=ShippingAddressDisplay.UpdateHeader(original,address);
        check(updated.Contains("آیدی: @recipient — نرگس امینی")&&updated.EndsWith(raw+"\n\n📦 آیتم‌های آماده ارسال\n🔴 تعداد آیتم‌ها: 1")&&address.FullAddress==raw,
            "recipient name is added beside the top identity without changing postal details, phone, postal code, items or stored raw address");
        check(ShippingAddressDisplay.UpdateHeader(updated,address)==updated,"recipient-name header update is idempotent");
        check(ShippingAddressDisplay.Identity("@recipient",address)=="@recipient — نرگس امینی","new shipping posts and previews use the same normalized recipient name");
        check(ShippingAddressDisplay.Identity("نرگس اميني",address)=="نرگس امینی","name-only customer labels are normalized without duplicating the same name");
        check(ShippingAddressDisplay.UpdateHeader("📦 درخواست ارسال مشترک\nگیرندهٔ اصلی: @recipient\n\n"+raw,address).Contains("گیرندهٔ اصلی: @recipient — نرگس امینی"),
            "shared parcels display the actual primary postal recipient beside its account");
        var manual=ShippingAddressDisplay.UpdateHeader("📦 درخواست ارسال دستی\n\nثبت‌کننده: 741471928\n\n"+raw,address);
        check(manual.Contains("ثبت‌کننده: 741471928 — گیرنده: نرگس امینی")&&ShippingAddressDisplay.UpdateHeader(manual,address)==manual,
            "manual addresses put the searchable postal recipient beside the registering admin with an explicit role label");
        var manager=ShippingAddressDisplay.UpdateHeader(manual,address,"@zahraa_frj");
        check(manager.Contains("ثبت‌کننده: @zahraa_frj — گیرنده: نرگس امینی")&&manager.EndsWith(raw)&&ShippingAddressDisplay.UpdateHeader(manager,address,"@zahraa_frj")==manager,
            "manual manager username replaces its numeric display without changing postal details or duplicating recipient names");
        var legacy=ShippingAddressDisplay.UpdateHeader("ثبت‌کننده: 741471928\nگیرندهٔ پستی: نرگس اميني\n\n"+raw,address,"@zahraa_frj");
        check(legacy=="ثبت‌کننده: @zahraa_frj — گیرنده: نرگس امینی\n\n"+raw,"legacy separate recipient header becomes one searchable line while preserving the full raw address");
        check(ShippingAddressDisplay.UpdateHeader("ثبت‌کننده: 123456\n\n"+raw,address).Contains("ثبت‌کننده: 123456 — گیرنده: نرگس امینی"),"other registrars retain their real identity instead of being relabelled as the manager");
        check(ShippingAddressDisplay.RecipientName(new(){ReceiverName="اميني",FullAddress="تهران خیابان نمونه"})=="امینی","a declared one-word recipient remains a valid display name");
        check(ShippingAddressDisplay.RecipientName(new(){ReceiverName="@recipient",FullAddress="تهران خیابان امینی پلاک ۷\n09120000000"}) is null,
            "street names and phone numbers are never promoted into postal recipient names");
        check(ShippingAddressDisplay.RecipientName(new(){ReceiverName="@recipient",FullAddress="نرگس امینی\nمریم کریمی"}) is null,
            "multiple unlabelled personal names stay unresolved rather than choosing the wrong recipient");
        check(ShippingAddressDisplay.RecipientName(new(){ReceiverName="@recipient",FullAddress="نام گیرنده: نرگس امینی\nبه نام: مریم کریمی"}) is null,
            "conflicting explicit recipient names are not silently guessed");
        check(ShippingAddressDisplay.NormalizeName("نرگس امیینی")=="نرگس امیینی","real extra letters and spelling differences are not silently changed by display normalization");
        var missing=new Address{ReceiverName="@recipient",FullAddress="09120000000"};
        check(ShippingAddressDisplay.UpdateHeader("ثبت‌کننده: 741471928\n\n09120000000",missing,"@zahraa_frj")=="ثبت‌کننده: @zahraa_frj\n\n09120000000","registrar username is displayed even if the recipient name is unresolved");
        check(ShippingAddressDisplay.UpdateHeader(original,missing)==original,"an unresolved recipient leaves the existing header intact");
    }
}
