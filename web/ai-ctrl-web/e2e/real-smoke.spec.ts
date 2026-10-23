import { expect, test } from '@playwright/test'

test.describe('Real server smoke', () => {
  test.skip(
    process.env.PLAYWRIGHT_REAL !== '1',
    'Set PLAYWRIGHT_REAL=1 to run against the real server.',
  )

  test('loads the authenticated fleet and reports the live VPN state without changing it', async ({
    page,
  }) => {
    await page.goto('/')

    await expect(page.getByRole('heading', { level: 1, name: 'AI Agents' })).toBeVisible()
    await expect(page).toHaveURL(/\/m\/[^/]+$/)

    const machineId = decodeURIComponent(new URL(page.url()).pathname.split('/').filter(Boolean)[1] ?? '')
    expect(machineId).not.toBe('')

    await page.goto(`/m/${encodeURIComponent(machineId)}/vpn`)

    await expect(page.getByRole('heading', { level: 1, name: 'VPN' })).toBeVisible()
    await expect(page.getByText('출구 IP')).toBeVisible()
    await expect(page.getByRole('button', { name: '연결 해제' })).toBeVisible()
    await expect(page.getByText('VPN Helper를 사용할 수 없습니다')).toHaveCount(0)
    await expect(page.getByText('VPN 로그인이 필요합니다')).toHaveCount(0)
    await expect(page.getByText('Proton VPN 앱', { exact: true })).toBeVisible()
    await expect(page.getByText(/[A-Z]{2}#\d+/).first()).toBeVisible()
  })
})
