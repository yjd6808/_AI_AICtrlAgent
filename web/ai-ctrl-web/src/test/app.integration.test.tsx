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

  it('redirects an authenticated root request to the machine console', async () => {
    renderApp('/')

    expect(
      await screen.findByRole('heading', { name: 'AI Agents' }),
    ).toBeInTheDocument()
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

  it('creates a session with an initial prompt', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await screen.findByRole('button', { name: '새 세션' })
    await user.click(screen.getByRole('button', { name: '새 세션' }))

    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    const directoryInput = within(panel).getByLabelText('워킹 디렉토리')
    await waitFor(() => expect(directoryInput).not.toHaveValue(''))

    await user.type(within(panel).getByLabelText('제목 (선택)'), '새 작업')
    await user.type(
      within(panel).getByLabelText('첫 프롬프트 (선택)'),
      '테스트를 실행해 주세요.',
    )
    await user.click(within(panel).getByRole('button', { name: '세션 만들기' }))

    expect(
      await screen.findByRole('heading', { name: '새 작업' }, { timeout: 7_000 }),
    ).toBeInTheDocument()
    expect(await screen.findByText('세션을 만들었습니다.')).toBeInTheDocument()
  })

  it('loads new session model options from the machine API', async () => {
    server.use(
      http.get('/api/v1/machines/mach_alpha/models', () =>
        HttpResponse.json({
          data: [
            {
              providerId: 'external-provider',
              modelId: 'external-model-x',
              name: 'External Model X',
            },
          ],
          meta: {
            source: 'live',
            asOf: new Date().toISOString(),
            machineOnline: true,
            stale: false,
            latencyMs: 1,
            traceId: 'test-models',
          },
        }),
      ),
    )

    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await user.click(await screen.findByRole('button', { name: '새 세션' }))
    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    const select = within(panel).getByLabelText('모델')

    expect(
      await within(select).findByRole('option', { name: 'External Model X' }),
    ).toBeInTheDocument()
  })

  it('selects a working directory from the machine filesystem tree', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await user.click(await screen.findByRole('button', { name: '새 세션' }))
    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    const directoryInput = within(panel).getByLabelText('워킹 디렉토리')
    await waitFor(() => expect(directoryInput).toHaveValue('E:\\Programming\\Mabi'))

    await user.click(within(panel).getByRole('button', { name: '경로 선택' }))
    const dialog = await screen.findByRole('dialog', { name: '폴더 선택' })

    await user.click(await within(dialog).findByRole('button', { name: 'Mabi' }))
    await user.click(await within(dialog).findByRole('button', { name: 'src' }))
    await user.click(within(dialog).getByRole('button', { name: '이 폴더 선택' }))

    await waitFor(() =>
      expect(directoryInput).toHaveValue('E:\\Programming\\Mabi\\src'),
    )
    expect(
      screen.queryByRole('dialog', { name: '폴더 선택' }),
    ).not.toBeInTheDocument()
  })

  it('browses a quick path as its own tree root', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await user.click(await screen.findByRole('button', { name: '새 세션' }))
    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    await user.click(within(panel).getByRole('button', { name: '경로 선택' }))
    const dialog = await screen.findByRole('dialog', { name: '폴더 선택' })

    const quickGroup = await within(dialog).findByRole('group', {
      name: '빠른 위치',
    })
    expect(
      within(quickGroup).getByText('C:\\Users\\jdyun\\Desktop'),
    ).toBeInTheDocument()

    await user.click(
      within(quickGroup).getByRole('button', { name: '바탕 화면' }),
    )
    expect(
      within(dialog).queryByRole('button', { name: 'Users' }),
    ).not.toBeInTheDocument()

    await user.click(
      await within(dialog).findByRole('button', { name: 'Projects' }),
    )
    await user.click(within(dialog).getByRole('button', { name: '이 폴더 선택' }))

    await waitFor(() =>
      expect(within(panel).getByLabelText('워킹 디렉토리')).toHaveValue(
        'C:\\Users\\jdyun\\Desktop\\Projects',
      ),
    )
  })

  it('navigates the directory picker by typed path and blocks denied paths', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await user.click(await screen.findByRole('button', { name: '새 세션' }))
    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    await user.click(within(panel).getByRole('button', { name: '경로 선택' }))
    const dialog = await screen.findByRole('dialog', { name: '폴더 선택' })

    const pathInput = within(dialog).getByLabelText('경로 직접 입력')
    await user.clear(pathInput)
    await user.type(pathInput, 'C:\\Windows')
    await user.click(within(dialog).getByRole('button', { name: '이동' }))

    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      /always denied/,
    )

    await user.clear(pathInput)
    await user.type(pathInput, 'C:\\Users\\jdyun')
    await user.click(within(dialog).getByRole('button', { name: '이동' }))

    await waitFor(() =>
      expect(within(dialog).getByLabelText('선택한 폴더 경로')).toHaveTextContent(
        'C:\\Users\\jdyun',
      ),
    )
    await user.click(within(dialog).getByRole('button', { name: '이 폴더 선택' }))

    await waitFor(() =>
      expect(within(panel).getByLabelText('워킹 디렉토리')).toHaveValue(
        'C:\\Users\\jdyun',
      ),
    )
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

  it('loads and updates the VPN failover policy', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/vpn')

    expect(
      await screen.findByRole('heading', { name: '자동 VPN 전환' }),
    ).toBeInTheDocument()
    expect(screen.getByText('할당량 소진')).toBeInTheDocument()

    const maxLoad = screen.getByLabelText('최대 부하')
    await user.clear(maxLoad)
    await user.type(maxLoad, '55')
    await user.click(screen.getByRole('button', { name: '정책 저장' }))

    expect(
      await screen.findByText('VPN 정책 저장 명령을 접수했습니다.'),
    ).toBeInTheDocument()
    await waitFor(() => {
      expect(getMockState().vpn.mach_alpha.policy.maxLoad).toBe(55)
    })
  })

  it('updates VPN recovery triggers and restart prompt', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/vpn')

    expect(
      await screen.findByRole('heading', { name: '오류 복구와 세션 재시작' }),
    ).toBeInTheDocument()
    const prompt = screen.getByLabelText('재시작 프롬프트')
    expect(prompt).toHaveValue('Continue from where you stopped. Do not repeat completed work.')

    await user.click(screen.getByLabelText('오류 복구 사용'))
    await user.click(screen.getByLabelText(/무료 할당량 소진/))
    await user.clear(prompt)
    await user.type(prompt, 'continue after failover')
    const cooldown = screen.getByLabelText('재시도 cooldown(초)')
    await user.clear(cooldown)
    await user.type(cooldown, '90')
    await user.click(screen.getByRole('button', { name: '복구 설정 저장' }))

    expect(
      await screen.findByText('복구 설정 저장 명령을 접수했습니다.'),
    ).toBeInTheDocument()
    await waitFor(() => {
      const recovery = getMockState().vpn.mach_alpha.recovery
      expect(recovery.enabled).toBe(true)
      expect(recovery.handleQuotaExceeded).toBe(false)
      expect(recovery.continuationPrompt).toBe('continue after failover')
      expect(recovery.cooldownSeconds).toBe(90)
    })
  })

  it('configures machine-wide failover from the new session panel', async () => {
    const user = userEvent.setup()
    renderApp('/m/mach_alpha/agents/inst_alpha_opencode')

    await user.click(await screen.findByRole('button', { name: '새 세션' }))
    const panel = await screen.findByRole('region', { name: '새 세션 만들기' })
    const failover = await within(panel).findByRole('region', {
      name: '오류 복구(failover) 설정',
    })

    const enabled = await within(failover).findByLabelText('오류 복구 사용')
    expect(enabled).not.toBeChecked()
    expect(
      within(failover).getByText('이 머신의 모든 세션에 적용됩니다.', {
        exact: false,
      }),
    ).toBeInTheDocument()

    await user.click(enabled)
    const prompt = await within(failover).findByLabelText('재시작 프롬프트')
    await user.clear(prompt)
    await user.type(prompt, 'resume from the new session panel')

    await user.click(within(failover).getByText('고급 설정'))
    const cooldown = within(failover).getByLabelText('재시도 cooldown(초)')
    await user.clear(cooldown)
    await user.type(cooldown, '45')

    await user.click(
      within(failover).getByRole('button', { name: '복구 설정 저장' }),
    )

    expect(
      await screen.findByText('복구 설정 저장 명령을 접수했습니다.'),
    ).toBeInTheDocument()
    await waitFor(() => {
      const recovery = getMockState().vpn.mach_alpha.recovery
      expect(recovery.enabled).toBe(true)
      expect(recovery.continuationPrompt).toBe('resume from the new session panel')
      expect(recovery.cooldownSeconds).toBe(45)
    })
  })

  it('configures the Discord webhook from settings', async () => {
    const user = userEvent.setup()
    renderApp('/settings')

    expect(
      await screen.findByRole('heading', { name: 'Discord 장애 알림' }),
    ).toBeInTheDocument()
    const webhook = await screen.findByLabelText('Webhook URL')
    await user.type(webhook, 'https://discord.com/api/webhooks/1/token')
    await user.click(screen.getByLabelText('Discord 알림 사용'))
    await user.click(screen.getByRole('button', { name: '알림 설정 저장' }))

    expect(
      await screen.findByText('Discord 알림 설정 저장 명령을 접수했습니다.'),
    ).toBeInTheDocument()
    await waitFor(() => {
      const recovery = getMockState().vpn.mach_alpha.recovery
      expect(recovery.discordEnabled).toBe(true)
      expect(recovery.discordWebhookConfigured).toBe(true)
    })
    expect(await screen.findByText('Webhook 설정됨')).toBeInTheDocument()
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
