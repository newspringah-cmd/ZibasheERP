import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'زیباشی | رابط وب آزمایشی',
  description: 'محیط مستقل زیباشی با داده‌های ساختگی',
  robots: {index:false, follow:false},
  icons: {icon:'/icon.svg'},
};

export default function RootLayout({children}: Readonly<{children: React.ReactNode}>) {
  return <html lang="fa" dir="rtl"><body>{children}</body></html>;
}
