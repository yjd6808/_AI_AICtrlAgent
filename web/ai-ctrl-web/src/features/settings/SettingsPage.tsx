import { useState } from 'react'
import { Copy, KeyRound, ShieldCheck, Trash2 } from 'lucide-react'
import type { MachineInfo } from '../../api/types'
import {
  useCreateEnrollmentCode,
  useMachines,
  useMe,
  useRevokeMachine,
} from '../../api/queries'
import { describeApiError } from '../../api/errors'
import { apiRoot } from '../../api/client'
import { formatAbsolute, formatRelative } from '../../api/format'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { ConfirmDialog } from '../../components/ConfirmDialog'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import { InlineNotice } from '../../components/InlineNotice'
import { PageHeader } from '../../components/PageHeader'
import { useToast } from '../../components/Toast'
import styles from './SettingsPage.module.css'

export function SettingsPage() {
  const toast = useToast()
  const me = useMe()
  const machines = useMachines()
  const enrollment = useCreateEnrollmentCode()
  const revoke = useRevokeMachine()
  const [revokeTarget, setRevokeTarget] = useState<MachineInfo | null>(null)

  const copyText = async (value: string, label: string) => {
    try {
      await navigator.clipboard.writeText(value)
      toast.notify('success', `${label}을 클립보드에 복사했습니다.`)
    } catch {
      toast.notify('error', '클립보드에 복사하지 못했습니다.')
    }
  }

  const generateCode = () => {
    enrollment.mutate(undefined, {
      onSuccess: () => toast.notify('success', '등록 코드를 생성했습니다.'),
      onError: (error) => toast.notify('error', describeApiError(error).message),
    })
  }

  const revokeMachine = () => {
    if (!revokeTarget) {
      return
    }
    revoke.mutate(revokeTarget.id.value, {
      onSuccess: () => {
        toast.notify('success', `${revokeTarget.hostname} 등록을 해제했습니다.`)
        setRevokeTarget(null)
      },
      onError: (error) => {
        toast.notify('error', describeApiError(error).message)
      },
    })
  }

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Settings"
        title="설정"
        description="계정, 머신 등록, 서버 연결 정보를 확인합니다."
        meta={
          import.meta.env.VITE_USE_MOCKS === 'true' ? (
            <Badge tone="warn">목 데이터</Badge>
          ) : (
            <Badge tone="ok">실서버 연결</Badge>
          )
        }
      />

      <section className={styles.panel}>
        <div className={styles.panelHeader}>
          <ShieldCheck size={16} aria-hidden="true" />
          <h2 className={styles.panelTitle}>계정</h2>
        </div>
        <div className={styles.infoGrid}>
          <div>
            <p className={styles.label}>이메일</p>
            <p className={styles.value}>{me.data?.email ?? '—'}</p>
          </div>
          <div>
            <p className={styles.label}>이름</p>
            <p className={styles.value}>{me.data?.name ?? '—'}</p>
          </div>
          <div>
            <p className={styles.label}>인증 시각</p>
            <p className={styles.value}>{formatAbsolute(me.data?.authTime)}</p>
          </div>
        </div>
        <p className={styles.hint}>
          로그인은 서버가 설정한 HttpOnly 쿠키로 유지됩니다. 이 앱은 인증 토큰을 저장하지 않으며
          로그아웃 API도 제공하지 않습니다.
        </p>
      </section>

      <section className={styles.panel}>
        <div className={styles.panelHeader}>
          <KeyRound size={16} aria-hidden="true" />
          <h2 className={styles.panelTitle}>머신 등록 코드</h2>
        </div>
        <p className={styles.hint}>
          새 Client를 등록할 때 사용할 일회성 코드를 생성합니다. 코드는 이 화면을 벗어나면 다시
          표시되지 않습니다.
        </p>
        <div className={styles.codeRow}>
          <Button
            tone="primary"
            size="sm"
            loading={enrollment.isPending}
            onClick={generateCode}
          >
            등록 코드 생성
          </Button>
          {enrollment.data ? (
            <>
              <code className={styles.code}>{enrollment.data.code}</code>
              <button
                type="button"
                className={styles.iconButton}
                aria-label="등록 코드 복사"
                onClick={() => void copyText(enrollment.data?.code ?? '', '등록 코드')}
              >
                <Copy size={14} aria-hidden="true" />
              </button>
            </>
          ) : null}
        </div>
        {enrollment.data ? (
          <p className={styles.expiry}>
            만료: {formatAbsolute(enrollment.data.expiresAt)} (
            {formatRelative(enrollment.data.expiresAt)})
          </p>
        ) : null}
        {enrollment.isError ? (
          <ErrorBlock
            error={enrollment.error}
            compact
            onRetry={generateCode}
            title="등록 코드를 생성하지 못했습니다"
          />
        ) : null}
      </section>

      <section className={styles.panel}>
        <div className={styles.panelHeader}>
          <h2 className={styles.panelTitle}>등록된 머신</h2>
          {machines.data ? <FreshnessBadge meta={machines.data.meta} /> : null}
        </div>
        {machines.isLoading ? (
          <LoadingBlock label="머신 목록을 불러오는 중…" />
        ) : machines.isError ? (
          <ErrorBlock error={machines.error} onRetry={() => void machines.refetch()} />
        ) : (machines.data?.data ?? []).length === 0 ? (
          <EmptyBlock
            title="등록된 머신이 없습니다"
            description="등록 코드를 생성하고 Client에서 등록을 완료하세요."
          />
        ) : (
          <ul className={styles.machineList}>
            {(machines.data?.data ?? []).map((machine) => (
              <li key={machine.id.value} className={styles.machineItem}>
                <div className={styles.machineInfo}>
                  <div className={styles.machineTitleRow}>
                    <p className={styles.machineName}>{machine.hostname}</p>
                    <Badge tone={machine.online ? 'ok' : 'danger'}>
                      {machine.online ? '온라인' : '오프라인'}
                    </Badge>
                  </div>
                  <p className={styles.machineMeta}>
                    {machine.id.value} · {machine.os} · Client {machine.clientVersion}
                  </p>
                  <p className={styles.machineMeta}>
                    마지막 접속 {formatRelative(machine.lastSeen)}
                    {machine.observedIp
                      ? ` · ${machine.observedIp}${machine.observedCountry ? ` (${machine.observedCountry})` : ''}`
                      : ''}
                  </p>
                  <p className={styles.machineId}>
                    등록 {formatRelative(machine.lastSeen)} · IP 소스 {machine.observedIpSource}
                  </p>
                </div>
                <Button
                  tone="danger"
                  size="sm"
                  icon={<Trash2 size={13} aria-hidden="true" />}
                  onClick={() => setRevokeTarget(machine)}
                >
                  등록 해제
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelTitle}>정보</h2>
        <div className={styles.infoGrid}>
          <div>
            <p className={styles.label}>API</p>
            <p className={styles.value}>{apiRoot}</p>
          </div>
          <div>
            <p className={styles.label}>Frontend</p>
            <p className={styles.value}>M5 · React 19 + Vite</p>
          </div>
          <div>
            <p className={styles.label}>모드</p>
            <p className={styles.value}>
              {import.meta.env.MODE} · mocks{' '}
              {import.meta.env.VITE_USE_MOCKS === 'true' ? 'on' : 'off'}
            </p>
          </div>
        </div>
        <InlineNotice tone="info" title="미구현 기능">
          실시간 UiHub, 파일 브라우저, VPN 제어, 진단, 클라이언트 로그, 세션 생성은 M4 API에
          없으므로 해당 화면은 준비 중 상태입니다.
        </InlineNotice>
      </section>

      <ConfirmDialog
        open={Boolean(revokeTarget)}
        title="머신 등록을 해제할까요?"
        description={
          revokeTarget ? (
            <>
              <strong>{revokeTarget.hostname}</strong> ({revokeTarget.id.value})의 등록을
              삭제합니다. 해당 Client는 다시 등록하기 전까지 서버에 연결할 수 없습니다.
            </>
          ) : null
        }
        confirmLabel="등록 해제"
        destructive
        busy={revoke.isPending}
        onConfirm={revokeMachine}
        onClose={() => {
          if (!revoke.isPending) {
            setRevokeTarget(null)
          }
        }}
      />
    </div>
  )
}
