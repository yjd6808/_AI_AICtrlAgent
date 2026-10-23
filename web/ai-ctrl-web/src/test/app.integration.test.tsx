import { describe, expect, it } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { getMockState } from '../mocks/fixtures'
import { server } from '../mocks/server'
import { renderApp } from './render'

function problemResponse(
  status: number,
  code: string,
  title: string,
  detail: string,
) {
  return HttpResponse.json(
    {
      type: `https://ai-ctrl.local/problems/${code}`,
      title,
      status,
      detail,
      instance: null,
      code,
    },
    { status },
  )
}

describe('application integration', () => {
  it('redirects unauthenticated users to the login page', async () => {
    server.use(
      http.get('/api/v1/auth/me', () =>
        problemResponse(401, 'unauthorized', '로그인이 필요합니다', '세션이 없습니다.'),
      ),
    )

    renderApp('/m/mach_alpha')

    expect(
      await screen.findByRole('heading', { name: '원격 제어 콘솔' }),
    ).toBeInTheDocument()
    expect(
      screen.getByRole('link', { name: /Google로 로그인/ }),
    ).toHaveAttribute('href', expect.stringContaining('/api/v1/auth/login'))
  })

  it('renders machines, instances, and read-only state', async () => {
    renderApp('/m/mach_alpha')

    expect(
      await screen.findByRole('heading', { name: 'AI Agents' }),
    ).toBeInTheDocument()
    expect(
      (await screen.findAllByText('OpenCode 127.0.0.1:4096')).length,
    ).toBeGreaterThan(0)
    expect(
      (await screen.findAllByText('Claude Code 127.0.0.1:4310')).length,
    ).toBeGreaterThan(0)
    expect((await screen.findAllByText('읽기 전용')).length).toBeGreaterThan(0)
  })

  it('disables prompt input when the machine is offline', async () => {
    renderApp(
      '/m/mach_gamma/agents/inst_gamma_opencode/sessions/ses_gamma',
    )

    expect(
      await screen.findByRole('heading', {
        name: '오프라인 머신의 캐시된 세션',
      }),
    ).toBeInTheDocument()
    expect(
      await screen.findByText(/캐시된 대화만 표시됩니다/),
    ).toBeInTheDocument()
    expect(screen.getByRole('textbox')).toBeDisabled()
  })

  it('offers abort-and-send for permission waiting sessions', async () => {
    renderApp(
      '/m/mach_alpha/agents/inst_alpha_opencode/sessions/ses_waiting',
    )

    expect(
      await screen.findByRole('heading', {
        name: '파일 업로드 권한 흐름 검토',
      }),
    ).toBeInTheDocument()
    expect(await screen.findByText('권한 승인 대기 중')).toBeInTheDocument()
    expect(
      screen.getByRole('button', { name: '중단 후 전송' }),
    ).toBeInTheDocument()
  })

  it('disables a shared-store session', async () => {
    renderApp('/m/mach_alpha/agents/inst_alpha_claude/sessions/ses_foreign')

    expect(
      await screen.findByRole('heading', {
        name: 'Desktop에서 실행 중인 세션',
      }),
    ).toBeInTheDocument()
    expect(
      await screen.findByText('읽기 전용 인스턴스'),
    ).toBeInTheDocument()
    expect(screen.getByRole('textbox')).toBeDisabled()
  })

  it('sends a prompt and follows the command to success', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode/sessions/ses_idle')

    expect(
      await screen.findByRole('heading', { name: '세션 정렬 규칙 점검' }),
    ).toBeInTheDocument()

    const input = screen.getByRole('textbox')
    await user.type(input, '테스트 프롬프트를 전송합니다.')
    const send = screen.getByRole('button', { name: '전송' })
    expect(send).toBeEnabled()
    await user.click(send)

    expect(
      await screen.findByText(
        '프롬프트가 전달되었습니다.',
        undefined,
        { timeout: 7_000 },
      ),
    ).toBeInTheDocument()
  })

  it('shows a forbidden state when access is denied', async () => {
    server.use(
      http.get('/api/v1/machines', () =>
        problemResponse(
          403,
          'forbidden',
          '접근 거부',
          '이 머신에 접근할 권한이 없습니다.',
        ),
      ),
    )

    renderApp('/')

    expect(
      await screen.findByText('접근이 거부되었습니다'),
    ).toBeInTheDocument()
  })

  it('shows a server error state when the machine list fails', async () => {
    server.use(
      http.get('/api/v1/machines', () =>
        problemResponse(
          500,
          'internal_error',
          '서버 오류',
          '머신 목록을 불러오지 못했습니다.',
        ),
      ),
    )

    renderApp('/')

    expect(await screen.findByText('서버 오류')).toBeInTheDocument()
  })

  it('renders the command history and command detail', async () => {
    renderApp('/commands')

    expect(
      await screen.findByRole('heading', { name: '명령 이력' }),
    ).toBeInTheDocument()
    expect(await screen.findByText('session.prompt')).toBeInTheDocument()
    expect(
      screen.getByText(/진행 중인 명령은 1초마다 갱신됩니다/),
    ).toBeInTheDocument()
  })

  it('browses workspace files and previews text', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/files')

    expect(
      await screen.findByRole('heading', { name: '워크스페이스 파일' }),
    ).toBeInTheDocument()
    expect((await screen.findAllByText('Mabi Tooling')).length).toBeGreaterThan(0)

    await user.click(await screen.findByRole('button', { name: 'README.md' }))

    const preview = await screen.findByRole('complementary', {
      name: '파일 미리보기',
    })
    expect(within(preview).getByText(/# Mabi Tooling/)).toBeInTheDocument()
    expect(within(preview).getAllByText('README.md').length).toBeGreaterThan(0)
  })

  it('creates a folder from the files toolbar', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/files')

    await screen.findByRole('heading', { name: '워크스페이스 파일' })
    await user.click(screen.getByRole('button', { name: '새 폴더' }))

    const dialog = screen.getByRole('dialog', { name: '새 폴더' })
    await user.type(within(dialog).getByLabelText('폴더 이름'), 'archive')
    await user.click(within(dialog).getByRole('button', { name: '만들기' }))

    expect(
      await screen.findByText('폴더 생성 명령을 접수했습니다.'),
    ).toBeInTheDocument()
    expect(
      await screen.findByRole('button', { name: 'archive' }),
    ).toBeInTheDocument()
  })

  it('uploads a file and shows it in the list', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/files')

    await screen.findByRole('heading', { name: '워크스페이스 파일' })
    await user.upload(
      screen.getByLabelText('파일 업로드'),
      new File(['hello'], 'hello.txt', { type: 'text/plain' }),
    )

    expect(
      await screen.findByText('hello.txt 업로드를 완료했습니다.'),
    ).toBeInTheDocument()
    expect(
      await screen.findByRole('button', { name: 'hello.txt' }),
    ).toBeInTheDocument()
  }, 15_000)

  it('uses proton app wording and hides helper conflicts in app mode', async () => {
    const vpn = getMockState().vpn.mach_alpha
    vpn.status = {
      ...vpn.status,
      usesProtonApp: true,
      helperAvailable: false,
      protonGuiRunning: true,
      username: null,
      statusText: 'Proton VPN 앱 · 터널 연결됨 (JP#277)',
    }

    renderApp('/m/mach_alpha/vpn')

    expect(
      await screen.findByRole('heading', { name: 'VPN' }),
    ).toBeInTheDocument()
    expect(screen.queryByText('VPN Helper를 사용할 수 없습니다')).not.toBeInTheDocument()
    expect(screen.queryByText('ProtonVPN GUI가 실행 중입니다')).not.toBeInTheDocument()
    expect(screen.getByText('Proton VPN 앱')).toBeInTheDocument()
  })

  it('follows VPN disconnect and reconnect commands', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/vpn')

    expect(
      await screen.findByRole('heading', { name: 'VPN' }),
    ).toBeInTheDocument()
    expect(await screen.findByText('터널 연결됨')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: '연결 해제' }))

    expect(
      await screen.findByText(
        'VPN 연결을 해제했습니다.',
        undefined,
        { timeout: 6_000 },
      ),
    ).toBeInTheDocument()
    expect(await screen.findByText('터널 끊김')).toBeInTheDocument()

    const connectButton = await screen.findByRole('button', {
      name: '일본 자동 연결',
    })
    await waitFor(() => expect(connectButton).toBeEnabled())
    await user.click(connectButton)

    expect(
      await screen.findByText(
        'VPN 연결 명령이 완료되었습니다.',
        undefined,
        { timeout: 6_000 },
      ),
    ).toBeInTheDocument()
    expect(await screen.findByText('터널 연결됨')).toBeInTheDocument()
  }, 15_000)
})
