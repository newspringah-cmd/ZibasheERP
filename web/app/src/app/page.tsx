import { TelegramShell } from '../components/telegram-shell';
import fixture from '../../test-fixtures/invoice-read.synthetic.json';
import { parseCustomerInvoicesRead } from '../lib/invoice-read-contract';

export default function Page() {
  if (fixture.synthetic !== true) {
    throw new Error('The static preview requires synthetic invoices.');
  }
  const invoicePage = parseCustomerInvoicesRead(fixture.response);
  return <TelegramShell invoicePage={invoicePage} />;
}
