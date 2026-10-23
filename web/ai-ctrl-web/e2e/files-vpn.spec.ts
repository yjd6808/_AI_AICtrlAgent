import { expect, test } from '@playwright/test'

test.describe('Files page', () => {
  test('browses folders and previews text without opening denied entries', async ({ page }) => {
    await page.goto('/m/mach_alpha/files')

    await expect(
      page.getByRole('heading', { level: 1, name: '워크스페이스 파일' }),
    ).toBeVisible()
    await expect(page.getByRole('button', { name: 'README.md', exact: true })).toBeVisible()

    const denied = page.getByRole('button', { name: /^\.env/ })
    await expect(denied).toBeDisabled()

    await page.getByRole('button', { name: 'docs', exact: true }).click()
    await expect(page).toHaveURL(/path=docs/)
    await expect(
      page.getByRole('navigation', { name: '경로' }),
    ).toContainText('docs')

    await page.getByRole('button', { name: 'guide.md', exact: true }).click()
    const preview = page.getByRole('complementary', { name: '파일 미리보기' })
    await expect(preview).toContainText('운영 가이드')
    await expect(page).toHaveURL(/preview=docs%2Fguide.md/)

    await preview.getByRole('button', { name: '미리보기 닫기' }).click()
    await expect(preview).toBeHidden()
  })

  test('creates a folder and uploads a file', async ({ page }) => {
    await page.goto('/m/mach_alpha/files')

    await expect(
      page.getByRole('heading', { level: 1, name: '워크스페이스 파일' }),
    ).toBeVisible()

    await page.getByRole('button', { name: '새 폴더', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: '새 폴더' })
    await dialog.getByLabel('폴더 이름').fill('e2e-folder')
    await dialog.getByRole('button', { name: '만들기' }).click()

    await expect(page.getByText('폴더 생성 명령을 접수했습니다.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'e2e-folder', exact: true })).toBeVisible()

    await page.getByLabel('파일 업로드').setInputFiles({
      name: 'e2e-upload.txt',
      mimeType: 'text/plain',
      buffer: Buffer.from('playwright upload\n', 'utf8'),
    })

    await expect(page.getByText('e2e-upload.txt 업로드를 완료했습니다.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'e2e-upload.txt', exact: true })).toBeVisible()
  })
})

test.describe('VPN page', () => {
  test('disconnects and reconnects to a selected server', async ({ page }) => {
    await page.goto('/m/mach_alpha/vpn')

    await expect(page.getByRole('heading', { level: 1, name: 'VPN' })).toBeVisible()
    await expect(page.getByText('출구 IP')).toBeVisible()
    await expect(page.getByText('203.0.113.8')).toBeVisible()

    await page.getByRole('button', { name: '연결 해제' }).click()
    await expect(page.getByText('VPN 명령 처리 중')).toBeVisible()
    await expect(page.getByText('VPN 연결을 해제했습니다.')).toBeVisible()

    await page.getByRole('combobox', { name: '국가', exact: true }).selectOption('DE')
    await expect(
      page.getByRole('heading', { level: 2, name: /독일/ }),
    ).toBeVisible()

    await page.getByRole('button', { name: /^DE#1/ }).click()
    await expect(page.getByText('선택한 서버')).toBeVisible()

    await page.getByRole('button', { name: 'DE#1 연결' }).click()
    await expect(page.getByText('VPN 연결 명령이 완료되었습니다.')).toBeVisible()
    await expect(page.getByRole('button', { name: '연결 해제' })).toBeVisible()
  })

  test('filters the server list by search text', async ({ page }) => {
    await page.goto('/m/mach_alpha/vpn')

    await expect(page.getByRole('heading', { level: 1, name: 'VPN' })).toBeVisible()
    await page.getByLabel('서버 검색').fill('Osaka')

    await expect(page.getByRole('button', { name: /^JP#4/ })).toBeVisible()
    await expect(page.getByRole('button', { name: /^JP#5/ })).toBeVisible()
    await expect(page.getByRole('button', { name: /^JP#1/ })).toBeHidden()
  })
})
