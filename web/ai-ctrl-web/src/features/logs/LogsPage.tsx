import { useQuery } from '@tanstack/react-query'
import { FileText, RefreshCw } from 'lucide-react'
import { Link, useParams } from 'react-router-dom'
import { fetchServerLog, fetchServerLogs } from '../../api/client'
import { formatRelative } from '../../api/format'
import { PageHeader } from '../../components/PageHeader'
import { EmptyBlock, ErrorBlock, LoadingBlock } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { FreshnessBadge } from '../../components/FreshnessBadge'
import styles from './LogsPage.module.css'

export function LogsPage() {
  const { machineId = '', name } = useParams<{ machineId: string; name?: string }>()
  const files = useQuery({
    queryKey: ['server-logs'],
    queryFn: ({ signal }) => fetchServerLogs(signal),
    refetchInterval: 30_000,
  })
  const selected = useQuery({
    queryKey: ['server-log', name],
    queryFn: ({ signal }) => fetchServerLog(name ?? '', 500, signal),
    enabled: Boolean(name),
  })

  if (files.isLoading) {
    return <LoadingBlock label="로그 목록을 불러오는 중" />
  }

  if (files.isError) {
    return <ErrorBlock error={files.error} onRetry={() => void files.refetch()} />
  }

  return (
    <div className={styles.page}>
      <PageHeader
        eyebrow="Server"
        title="로그"
        description="서버 로그 파일을 선택해 최근 500줄을 확인합니다."
        meta={<FreshnessBadge meta={files.data?.meta} />}
        actions={
          <Button
            tone="secondary"
            size="sm"
            icon={<RefreshCw size={15} />}
            loading={files.isFetching || selected.isFetching}
            onClick={() => {
              void files.refetch()
              if (name) {
                void selected.refetch()
              }
            }}
          >
            새로고침
          </Button>
        }
      />

      <div className={styles.layout}>
        <nav className={styles.files}>
          {files.data?.data.length === 0 ? (
            <EmptyBlock title="로그 파일이 없습니다" description="아직 생성된 서버 로그가 없습니다." />
          ) : (
            files.data?.data.map((file) => (
              <Link
                key={file.name}
                to={`/m/${encodeURIComponent(machineId)}/logs/${encodeURIComponent(file.name)}`}
                className={name === file.name ? styles.fileActive : styles.file}
              >
                <FileText size={14} />
                <span className={styles.fileName}>{file.name}</span>
                <span className={styles.fileMeta}>
                  {formatRelative(file.modifiedAt)} · {Math.ceil(file.sizeBytes / 1024)} KB
                </span>
              </Link>
            ))
          )}
        </nav>

        <section className={styles.viewer}>
          {!name ? (
            <EmptyBlock title="로그 파일을 선택하세요" description="왼쪽 목록에서 파일을 선택합니다." />
          ) : selected.isLoading ? (
            <LoadingBlock label="로그를 불러오는 중" />
          ) : selected.isError ? (
            <ErrorBlock error={selected.error} onRetry={() => void selected.refetch()} />
          ) : (
            <>
              {selected.data?.data.truncated ? (
                <p className={styles.truncated}>파일이 커서 마지막 500줄만 표시합니다.</p>
              ) : null}
              <pre className={styles.content}>{selected.data?.data.content}</pre>
            </>
          )}
        </section>
      </div>
    </div>
  )
}
